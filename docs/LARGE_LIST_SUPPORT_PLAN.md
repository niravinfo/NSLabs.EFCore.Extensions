# Large `IN` list support plan — 5000-id filters without hitting parameter limits

Status: PLAN ONLY — no code changed.
Scope: `Where(x => ids.Contains(x.Prop))` (and `!ids.Contains(...)`) inside bulk `Update` / `Delete` (and upsert guards) on all three providers.
Goal: a 5000-id filter costs **1 parameter**, never throws, and uses the per-provider fast path EF Core itself uses.

---

## 1. Answer: do we support large lists today? No.

All three generators materialize `Contains` into `SqlInNode(Property, Values)` and then count **one parameter per value**:

- `src/NSLabs.EFCore.Extensions/Internal/LinqPredicateTranslator.cs:248-249,261-264` — `list.Contains(x.Prop)` → `new SqlInNode(prop, values)` with each element already passed through `ModelBinder.ConvertToProvider`.
- `src/NSLabs.EFCore.Extensions/Internal/SqlNodes.cs:116-123` — `SqlInNode` carries `IReadOnlyList<object?> Values`.
- Counting (identical in all three generators):
  - `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs:422` — `SqlInNode inNode => inNode.Values.Count`
  - `src/NSLabs.EFCore.Extensions.Sqlite/Internal/SqliteSqlGenerator.cs:291` — same
  - `src/NSLabs.EFCore.Extensions.Npgsql/Internal/NpgsqlSqlGenerator.cs:295` — same
- Emission (identical shape in all three): `col IN (@p0, @p1, …)`:
  - SQL Server `SqlServerSqlGenerator.cs:621-640` (`EmitIn`)
  - SQLite `SqliteSqlGenerator.cs:499-521`
  - Npgsql `NpgsqlSqlGenerator.cs:527-551`

Chunking only splits **between operations**, never **inside one predicate**. A single op whose cost exceeds the budget throws:

| Provider | Budget | Throw site | 5000-id result today |
|---|---|---|---|
| SQL Server | `BulkExecuteOptions.MaxParametersPerCommand = 2000` default (`src/NSLabs.EFCore.Extensions/BulkExecuteOptions.cs:5`), hard cap 2100 | `SqlServerSqlGenerator.cs:31-37` | `Update … Where(ids.Contains) + 1 SET` costs ~5001 params → `InvalidOperationException: requires 5001 parameters which exceeds MaxParametersPerCommand=2000` |
| SQLite | `effectiveLimit = min(user, 999)` (`SqliteSqlGenerator.cs:8,16`) | `SqliteSqlGenerator.cs:30-34` | Same throw, limit 999 (`SQLITE_MAX_VARIABLE_NUMBER`). Even `MaxParametersPerCommand = 2000` clamps to 999. |
| PostgreSQL | `effectiveLimit = min(user, 65535)` (`NpgsqlSqlGenerator.cs:8,17`) | `NpgsqlSqlGenerator.cs:30-35` | With default 2000 → same throw. If the user raises the budget (e.g. 20000), it emits `IN (@p0…@p4999)` — works but slow: 5000-param SQL, plan-cache bloat, wire overhead. |

So EF Core handles `ids.Contains` with 5000 ids easily; we throw (SQL Server / SQLite / PG-default) or degrade (PG-raised-budget).

Existing tests lock in the current small-list behavior and the throw behavior:

- Small `IN` golden SQL: `tests/.../ValidationTests.cs:179-182` (`IN (` + `LIKE` + `OR`).
- Single-op-over-budget throws: SQL Server `ChunkingTests.cs:51-64`, SQLite `SqliteChunkingTests.cs:17-30`, Npgsql `NpgsqlChunkingTests.cs:17-29`.
- SQLite clamp: `SqliteChunkingTests.cs:4-15`.

---

## 2. What EF Core does (the fast method per provider — verified against EF8/9/10 docs)

This repo pins EF Core 10 (`Directory.Packages.props:9-12`). EF's behavior changed across versions, so the plan tracks all three:

- **SQL Server — `OPENJSON`, single `NVARCHAR(MAX)` JSON param (EF8/9 default; EF10 auto-fallback when near 2100):**
  ```sql
  WHERE [b].[Id] IN (SELECT [i].[value] FROM OPENJSON(@__ids_0) WITH ([value] int '$') AS [i])
  ```
  Param: `@__ids_0='[1,2,3]'`. Source: EF8 breaking-change notes + EF8 Preview 4 blog ("Better Contains queries"). EF10 keeps this as the overflow strategy: default is `IN (@p1,@p2…)` with padding, and it "automatically switches to OPENJSON when needed" past the 2100 limit (dotnet/efcore#32394; EF10 "Improved translation for parameterized collection"). Requires compat level 130+ (SQL Server 2016+). Known pitfall: string columns with non-database collation can hit collation conflict (dotnet/efcore#32147) — noted as a risk in §7.
- **PostgreSQL (Npgsql) — `= ANY`, single native array param:**
  ```sql
  WHERE b."Id" = ANY (@__ids_0)   -- @__ids_0 :: integer[]
  ```
  How Npgsql does it: the .NET list is sent as **one array-typed parameter** (binary array encoding for `int[]`/`long[]`/etc.), and the server compares with the `ScalarArrayOp` `= ANY`. This is Npgsql's documented translation — *"arrayNonColumn.Contains(element) → element = ANY(arrayNonColumn) — Can use regular index"* (npgsql.org `efcore/mapping/array.html`, verified §2.4-S1) — and the translation Microsoft's EF blog attributes to the Npgsql provider: *"we pass the array of blog names as a SQL parameter directly to ANY"* (EF8 Preview 4, §2.4-S2). Complex compositions over parameters use `unnest(@p)` (Npgsql EF 8.0 release notes, §2.4-S3); plain `Contains` is `= ANY`, which is what this plan implements.
  Scale math: the 65535 protocol cap counts **parameters, not array elements** — 5000 ids = 1 param, 50000 ids = still 1 param (~200 KB binary for 50k ints, far under the 1 GB field limit). The `IN (@p0…@pN)` alternative would need 5000–50000 params, ~hundreds of KB of SQL text, a fresh plan per cardinality, and at 50k it nearly exhausts the protocol budget. Within a single-statement `UPDATE … WHERE` (this library's design constraint), `= ANY` is therefore the optimal shape at 5k and still optimal at 50k. The only shape that can beat it past ~tens of thousands of ids is a temp staging table (`COPY` + `ANALYZE` + `JOIN`, which gives the planner statistics) — but that needs extra round trips and DDL, so it belongs to the existing `DESIGN.md` Strategy B staging-table fast path, not to v1 of this plan.
- **SQLite — `json_each`, single JSON-text param:**
  ```sql
  WHERE "Id" IN (SELECT "value" FROM json_each(@p0))   -- @p0 = '[1,2,3]'
  ```
  One `TEXT` param. Why `json_each` specifically, and why it is the best single-statement choice (not our invention):
  - The cap forces a single-param shape: `SQLITE_MAX_VARIABLE_NUMBER` *"defaults to 999 for SQLite versions prior to 3.32.0 (2020-05-22) or 32766 for SQLite versions after 3.32.0"* (sqlite.org `limits.html`, §2.4-S6). Our generator conservatively clamps to 999 (§3), so any N-param `IN` above a few hundred ids is impossible in one statement — 5000 ids would need 6+ chunks/statements through our sequential `SqliteExecutor`.
  - `json_each` is SQLite's own unpacking mechanism: one of only *"two table-valued functions that can be used to decompose a JSON string"*, with output column `value ANY` holding *"INTEGER, REAL, or TEXT depending on the type of the corresponding JSON field"* — i.e. integers compare as integers with no cast (sqlite.org `json1.html`, §2.4-S7). It is the direct SQLite analog of `OPENJSON`/`unnest`, and the only in-engine way to turn one parameter into a rowset: EF10's framing is exactly this — the JSON array is *"unpacked using the SQL Server OPENJSON function (other databases use similar mechanisms)"* (EF10 "What's New", §2.4-S4).
  - Honesty note: no official SQLite benchmark crowns `json_each` the fastest shape at every size — the claim here is narrower and sourced: above the variable cap it is the only single-statement shape, and below the cap we keep plain `IN` (matching EF10's multi-param default) and let our own benchmark (§8) lock the exact switch mark.

Decision: mirror EF — **OPENJSON (SQL Server) / `= ANY` (PG) / `json_each` (SQLite)**.

### 2.4 Sources (all verified; plan claims trace to these)

- S1 — Npgsql array-operation table: `arrayNonColumn.Contains(element)` → `element = ANY(arrayNonColumn)`, *"Can use regular index"*: `https://www.npgsql.org/efcore/mapping/array.html`
- S2 — EF8 Preview 4, "Better Contains queries": JSON-array param + `OPENJSON` on SQL Server; *"we pass the array … directly to ANY"* on PostgreSQL: `https://devblogs.microsoft.com/dotnet/announcing-ef8-preview-4`
- S3 — Npgsql EF 8.0 release notes: `unnest(@p)` for composed parameter collections (complex case; simple `Contains` stays `= ANY`): `https://www.npgsql.org/efcore/release-notes/8.0.html`
- S4 — EF10 "What's New", parameterized collections: `IN (@p…)` new default with padding; JSON array *"unpacked using the SQL Server OPENJSON function (other databases use similar mechanisms)"*; `ParameterTranslationMode` control: `https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew`
- S5 — `ParameterTranslationMode` enum (`MultipleParameters` = `IN (@p…)`, `Parameter` = single array-like param e.g. `OPENJSON(@p)`, `Constant`): `https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.parametertranslationmode?view=efcore-10.0`
- S6 — SQLite variable cap: `SQLITE_MAX_VARIABLE_NUMBER` *"defaults to 999 … prior to 3.32.0 … or 32766 … after 3.32.0"*: `https://sqlite.org/limits.html`
- S7 — SQLite `json_each`: table-valued function decomposing a JSON string; `value` column typed per JSON field: `https://sqlite.org/json1.html`
- S8 — EF8→EF10 rationale for the hybrid itself: *"Contains to OPENJSON translation regresses performance … We have plans to switch to WHERE IN (@p1, @p2) by default in 10 … (since SQL Server has a 2100 parameter limit, we'll automatically switch to OPENJSON when needed)"*: `https://github.com/dotnet/efcore/issues/32394`
- S9 — PostgreSQL `ANY`/`SOME`/`ALL` formal semantics: `https://www.postgresql.org/docs/current/functions-comparisons.html`

---

## 3. The "mark" (threshold): hybrid, and yes — it is a performance decision

Yes: the mark is chosen performance-wise, per provider — not just to dodge the cap. This mirrors EF10's explicit tradeoff (`ParameterTranslationMode.MultipleParameters` default for plan quality vs `Parameter` single-param mode):

- **Small `IN (@p0…)`**: the optimizer sees each value (better cardinality estimates), seeks indexes directly, no JSON-parse / array-materialize overhead. Fastest for tens of values.
- **Large single-param path (`OPENJSON` / `= ANY` / `json_each`)**: parse cost is fixed and small, while N-param SQL costs grow linearly (text size, wire bytes, plan-cache variants, and for SQL Server/SQLite the hard cap). Fastest for hundreds-to-thousands of values.

So:

- **Keep `IN (@p…)` for small lists** (existing golden SQL unchanged, best plan quality).
- **Switch to the single-param fast path when EITHER is true:**
  1. `Values.Count > Threshold` (the "mark"), **OR**
  2. expanding to N params would overflow the op's share of the effective budget (correctness backstop — a 5000-id list must succeed even if someone sets `Threshold = 10000`).

Nulls need no special counting: a null element is one more value on the slow path (one `@p NULL` param, exactly as today) and one more `null` element in the JSON/array payload on the fast path — see §4.1 for why we keep EF parity instead of adding `IS NULL` branches.

Proposed default marks — **starting values, not claimed optima.** No vendor publishes "switch at N" numbers; the hybrid shape itself is sourced (S8: EF regressed small queries with always-`OPENJSON` and moved the default back to `IN` with auto-`OPENJSON` past the cap), while the exact marks below are our initial settings to be locked by the benchmark in §8 (measure `IN` vs fast path at 10/50/100/500/5000 on each provider; keep whichever wins per size):

| Provider | Proposed `Threshold` | Sourced rationale |
|---|---|---|
| SQL Server | `100` values | S8 justifies hybrid over always-`OPENJSON`; below ~100, per-value params are cheap and cardinality-accurate, above they bloat text/plans toward the 2100 cap. Overflow backstop guarantees cap compliance regardless of where the mark lands. |
| SQLite | `50` values | S6 forces single-param above a few hundred ids regardless; S4's multi-param default keeps small `IN` for plan quality below. 50 is early because our 999 clamp is conservative (modern engines allow 32766, but we do not probe) and `json_each` parse is cheap — benchmark confirms. |
| PostgreSQL | `50` values (or always-`ANY` — decide in implementation) | S1 (`ANY` *"can use regular index"*) means `ANY` is competitive even small; the only reason to keep small-`IN` is golden-SQL stability. If benchmarks show `ANY` never regresses, simplify PG to always-`ANY`. |

**SQLite 999 hard limit — no issue after this change.** `effectiveLimit = min(user, 999)` stays as-is (safe default; newer SQLite builds allow 32766 but we do not probe for it in v1). It cannot break large lists because the decision runs *before* the overflow check: a 5000-id `IN` becomes 1 param (`json_each`) so the op costs ~2 params total, far under 999. Small lists (≤50) still cost N params and are still budget-checked exactly as today — e.g. 40 ids + 1 SET + 1 discriminator = 42 ≤ 999, one chunk; a hypothetical 900-id list with `Threshold = int.MaxValue` still hits the backstop (`900 + other > 999` → fast path → 1 param). The clamp is therefore never a correctness problem, only a (rarely reached) trigger for the fast path.

No new public option in v1: keep the mark as `internal const` per generator plus the automatic overflow backstop. If users later ask for control (mirroring EF's `UseParameterizedCollectionMode` / `EF.Parameter` / `EF.Constant`), add `BulkExecuteOptions.LargeListThreshold` then — not now, to avoid API bloat before measurements.

Consequence for counting: `CountParameterNodes(SqlInNode)` can no longer be `Values.Count` unconditionally. It must return `1` when the fast path will be taken and `Values.Count` otherwise (null elements count as values on the slow path, exactly as today) — using the **same predicate** the emitter uses, or chunk plans and emitted params will disagree.

---

## 4. Detailed design per provider

### 4.1 Shared: null rule (EF parity — no `IS NULL` branches) + decision function + cost accounting

How EF Core handles nulls in `Contains` — the direct answer: **it doesn't compensate for them.** A parameterized `ids.Contains(e.Col)` translates to a plain `IN (SELECT …)` / `IN (@p…)` / `= ANY (@p)` with no extra null predicate. A `NULL` column row therefore never matches, even when the list contains null (SQL three-valued logic: `NULL IN (…)` is UNKNOWN). Evidence:

- EF team on `InExpression`-with-subquery nulls: *"our null semantics around InExpression with subquery currently seems broken (fortunately it's dead code): IN returns null when the item is null (or when the values contains null and a match isn't found). We'd need to make that logic actually work."* (dotnet/efcore#30955).
- EF11 `JSON_CONTAINS` (the successor fast path): *"does not support searching for null values… only applied when EF can determine that at least one side is non-nullable… When this cannot be determined, EF falls back to the previous OPENJSON-based translation"* — which likewise never matches nulls (EF Core 11 "What's New").
- An early null-compensating proposal (`([s].[Value] = [e].[Code]) OR ([s].[Value] IS NULL AND [e].[Code] IS NULL)`, dotnet/efcore#13617 discussion) was **not** what shipped; EF8+ shipped plain `IN (SELECT [value] FROM OPENJSON(…))`.

Consequences for this plan (review decision — the earlier `OR col IS NULL` draft is withdrawn):

- **No `IS NULL` / `IS NOT NULL` branches are added, on either the slow or the fast path.** Null list elements ride along exactly as today: one `@p NULL` param on the slow path; one JSON `null` element (SQL Server/SQLite) or one array `NULL` element (PG) on the fast path. Observable behavior is then identical across slow path, fast path, all three providers, today's code, and EF Core: **nulls never match.**
- This is also a non-issue for the target scenario: the 5000-ids filter is near-always `List<int>`/`List<long>`/`List<Guid>` against a non-nullable PK, where neither side can be null at all. Nulls can only arise with a nullable property (`int?`, `string`) paired with a `List<int?>`/`List<string>` containing null — same narrow edge where EF itself doesn't match either.
- One implementation note this forces: the PG typed-array builder must tolerate nulls — when `Values` contains null and the element type is a non-nullable struct, build the nullable array (`int?[]`, `Guid?[]`, …) so the `NULL` element survives; `System.Text.Json` writes a null element as `null` with no extra work. `byte[]` **elements** stay excluded from all fast paths in v1 (§4.2).

Decision helper (core assembly, provider-neutral on inputs; counts all values including nulls, exactly like today's `Values.Count`):

```csharp
internal static bool UseFastInPath(int valueCount, int effectiveLimit, int otherParamsInOp, int threshold)
    => valueCount > threshold
    || valueCount + otherParamsInOp > effectiveLimit;   // overflow backstop
```

- `effectiveLimit`: SQL Server = `MaxParametersPerCommand`; SQLite = `min(user, 999)`; PG = `min(user, 65535)`.
- `otherParamsInOp`: assignments + other predicate parts + discriminator (already counted today). The backstop needs the op-level total, so compute op cost in two passes — first sum non-`IN` params, then decide each `IN` node. A single `IN` of 5000 ids with 1 assignment: `other = 1`, `5000 + 1 > 2000` → fast path → op cost becomes `1 + 1 = 2`.
- Negation arrives as `SqlNotNode(SqlInNode)` (see §6). The decision function must look through `SqlNotNode` when counting, and the emitter must emit the negated fast form.
- Empty list: unchanged — `IN` → `1=0`, `NOT IN` → `NOT (1=0)`, zero params. Fast path never triggers for 0 values.

### 4.2 SQL Server — `OPENJSON`

Locked decision per review: **no compatibility-level check, no fallback.** Baseline is compat 150+ (`OPENJSON` exists since 130 / SQL Server 2016), so the generator emits `OPENJSON` unconditionally for large lists. On an ancient database the server itself throws — that is the intended behavior, not a library fallback path. (`SqlServerProvider`'s compat resolution stays untouched for the existing `LEAST`/`GREATEST` gate; it is not consulted for `IN`.)

Small (`valueCount ≤ threshold`, fits budget) — byte-identical to today, nulls included:

```sql
[Id] IN (@p0, @p1, @p2)
NOT ([Id] IN (@p0, @p1))   -- via SqlNotNode wrapper, as today
```

Large — one `NVARCHAR(MAX)` JSON param carrying the values as-is (null elements serialize as JSON `null`, preserving today's/EF's never-match null semantics):

```sql
[Id] IN (SELECT [v].[value] FROM OPENJSON(@p7) WITH ([value] int '$') AS [v])
NOT ([Id] IN (SELECT [v].[value] FROM OPENJSON(@p7) WITH ([value] int '$') AS [v]))
```

Type map for the `WITH ([value] <type> '$')` clause, derived from EF metadata (`IProperty.GetColumnType()` / relational type mapping; fallback by CLR type of the **provider-converted** values):

| CLR / store | `WITH` type |
|---|---|
| `int` | `int` |
| `long` | `bigint` |
| `short` | `smallint` |
| `byte` | `tinyint` |
| `bool` | `bit` |
| `Guid` | `uniqueidentifier` |
| `DateTime/DateTimeOffset/DateOnly/TimeOnly` | `datetime2` / `datetimeoffset` / `date` / `time` (match column type; JSON carries ISO-8601 strings) |
| `decimal` | `decimal(38,18)` or the column's configured precision/scale |
| `double/float` | `float` |
| `string` | `nvarchar(max)` (or the column length, e.g. `nvarchar(450)` — mirror what EF emits) |
| enum | underlying numeric type (values are already converted by `ConvertToProvider`) |

JSON building: serialize the **provider-converted** values (`SqlInNode.Values` are already converted at translate time, nulls included) with **`System.Text.Json` — not a hand-rolled `StringBuilder`**. Correctness review decision: manual JSON is rejected for this path. Data can be any mapped type (strings with quotes/backslashes/control chars, decimals where the OS locale uses `,` as separator, `DateTime`/`DateOnly`/`Guid`, etc.) — a simple escaper will break on some of it, and a fully-correct manual writer just re-implements `System.Text.Json` worse. `System.Text.Json` is the same serializer EF Core itself relies on across its JSON support, is culture-invariant by default (numbers/dates serialize per JSON spec, never per OS locale), handles all CLR primitives including `DateOnly`/`TimeOnly` on .NET 10, and is itself heavily optimized (UTF-8, pooled buffers, SIMD) — a manual builder has no meaningful performance edge once the dominant costs (TDS round-trip + `OPENJSON` parse) are counted, and it carries all of the escaping risk. Concretely: `JsonSerializer.Serialize<IReadOnlyList<object?>>(values)` (or the typed `List<T>` overload where the element type is known) with default options; a null element serializes as JSON `null`, which yields an `OPENJSON` NULL row that never matches — exactly today's `@p NULL` behavior. Known edge: `byte[]` **elements** (i.e. `List<byte[]>` against a `varbinary` column) — `System.Text.Json` serializes each `byte[]` as a base64 string, which does not round-trip through `WITH ([value] varbinary…)` without extra work. Rare path: keep `byte[]`-element lists on the multi-param `IN` route in v1; if such an op would overflow the budget, throw the existing overflow error with guidance (acceptable for v1; revisit only if requested).
Param sending: value is a .NET `string`; executors today do `dbParam.Value = value` with no `DbType`. For >4000-char JSON this must be `NVARCHAR(MAX)`: set `SqlDbType.NVarChar, Size = -1` when the connection is `Microsoft.Data.SqlClient`-backed (detect via parameter type name, no new package ref — core stays provider-neutral; do it in `SqlServerExecutor` which already lives in the SqlServer package). Verify `Microsoft.Data.SqlClient` infers MAX correctly without the hint; add the hint only if tests show truncation at 4000.

### 4.3 PostgreSQL — `= ANY (@p)`

Small — byte-identical to today:

```sql
"Id" IN (@p0, @p1)
NOT ("Id" IN (@p0, @p1))
```

Large — one typed array param carrying the values as-is (null elements become array `NULL`s, preserving today's/EF's never-match null semantics):

```sql
"Id" = ANY (@p7)
NOT ("Id" = ANY (@p7))     -- keep the NOT wrapper; do NOT rewrite to <> ALL
```

Array building: `SqlInNode.Values` are provider-converted `object?`, nulls included. Build a **typed CLR array** of the element type (int→`int[]`, long→`long[]`, string→`string[]`, Guid→`Guid[]`, …) — and when nulls are present with a non-nullable struct element, the **nullable** array (`int?[]`, `Guid?[]`, …) so the `NULL` elements survive. Deriving the element type: use the first non-null value's runtime type, falling back to the property CLR type (unwrapping nullable/enum → underlying). Npgsql infers the PG array type from the CLR array — no `NpgsqlDbType` reference needed in code (avoid a new compile dependency); verify with integration tests that `int[]`, `long[]`, `string[]`, `Guid[]`, `DateTime[]` bind correctly through `connection.CreateCommand()` (the executors use provider-agnostic `CreateParameter`, which returns `NpgsqlParameter` on a PG connection — inference happens there). If inference fails for any type (e.g. enums pre-conversion — shouldn't happen since values are converted), set the type via reflection-free duck-typing in `NpgsqlExecutor` (already lives in the Npgsql package, so referencing `NpgsqlTypes` there is acceptable if needed).
Empty (no values at all): `1=0` / `NOT (1=0)` as today.
`unnest` is NOT needed for plain `Contains`; reserve it for future composed operators (`Any` over lists — out of scope).

### 4.4 SQLite — `json_each`

Small — byte-identical to today:

```sql
"Id" IN (@p0, @p1)
NOT ("Id" IN (@p0, @p1))
```

Large — one JSON `TEXT` param carrying the values as-is (null elements serialize as JSON `null`, preserving today's/EF's never-match null semantics), serialized with `System.Text.Json` (same rationale as §4.2 — no manual JSON):

```sql
"Id" IN (SELECT "value" FROM json_each(@p7))         -- @p7 = '[1,2,3]'
NOT ("Id" IN (SELECT "value" FROM json_each(@p7)))
```

Notes: `json_each(@p)` with a JSON array binds the array via the parameter — no string interpolation. `value` column carries JSON values with native affinity (integers compare as integers). JSON1 is compiled into `Microsoft.Data.Sqlite`'s bundled SQLite on all supported platforms; add one integration probe (`SELECT count(*) FROM json_each('[1,2]')`) so an exotic native build fails loudly with a clear message. Same `byte[]`-element exception as SQL Server: keep binary-element lists on the multi-param route in v1.

---

## 5. Files to touch (implementation checklist)

1. `Internal/SqlNodes.cs` — no shape change needed (keep `SqlInNode(Property, Values)`; `Negated` stays unused). Optional: add `ElementType` cache — prefer deriving at emit time to avoid translator changes.
2. `Internal/LinqPredicateTranslator.cs` — **no change** (still materializes `Values`, nulls included; conversion stays here).
3. **New** `Internal/LargeListHelper.cs` (core, provider-neutral): threshold consts or per-provider args, `ShouldUseFastPath(...)` (value count + backstop), JSON payload builder via `System.Text.Json` over converted values (nulls included), typed-array builder for PG (`Array BuildTypedArray(IProperty, values)` — nullable array when nulls present with struct elements; `byte[]` elements excluded → caller keeps IN route).
4. `SqlServerSqlGenerator.cs` — `CountParameterNodes` + `EmitIn` gain fast path (§4.1–4.2); `Generate`'s single-op overflow check (`:31-37`) must use the **fast-aware** cost or a 5000-id op still throws before emitting. Same for the upsert-guard path (`ExpandUpsert` fixed-cost calc uses `CountParameterNodes(spec.Guard)` — automatically fixed once counting is fast-aware). No compat-level wiring for `IN`.
5. `SqliteSqlGenerator.cs` — same two functions (§4.1 + §4.4); overflow check `:30-34` uses the fast-aware cost (this is what makes the 999 clamp harmless for large lists).
6. `NpgsqlSqlGenerator.cs` — same two functions (§4.1 + §4.3); overflow check `:30-35`. Handle `EmitQualified` too (upsert-guard path uses `EmitQualified`; `IN`/`ANY` emission must qualify the column there).
7. `SqlServerExecutor.cs` — ensure JSON param goes as `NVARCHAR(MAX)` (test; add `SqlDbType` hint only if needed).
8. `NpgsqlExecutor.cs` — verify array inference; add explicit typing only if a type fails.
9. `SqliteExecutor.cs` — no change expected (TEXT param as today).
10. Docs: `README.md` (one line under batching: large `IN` lists use single-param fast path) + `docs/DESIGN.md` §4 chunking paragraph (replace "~2100 params/request, 4 params → ~500 ops" example with large-`IN` note).

---

## 6. Semantics that must NOT change (nulls included)

- `!ids.Contains(x.Id)` stays a `NOT (…)` wrapper — today via `SqlNotNode(SqlInNode)` (`LinqPredicateTranslator.cs:20-21` + `Emit … SqlNotNode not => $"NOT ({…})"` in all three emitters). Both small and fast paths keep the wrapper; never rewrite to `NOT IN`/`<> ALL`.
- **Nulls: EF parity, no new predicates.** A null list element is carried exactly as today (`@p NULL` on the slow path; JSON `null` / array `NULL` on the fast path) and never matches a `NULL` row — identical across slow path, fast path, all three providers, today's code, and EF Core (§4.1). No `OR col IS NULL` is added. Rationale: the target scenario (5000 ids against a non-nullable PK) cannot contain nulls at all; the nullable edge copies EF rather than inventing C#-but-not-EF semantics.
- Empty list (zero values): `EmitIn` early-returns `1=0` / `NOT (1=0)` (all three generators) — keep, zero params.
- Value converters / enums: values are converted at translate time (`ConvertToProvider`); JSON/array builders must consume the **converted** values, never re-convert. Serialization input to `System.Text.Json` is therefore plain primitives (numbers, strings, bools, ISO-8601 date strings, Guid strings).
- Discriminator filter (`ModelBinder.AddDiscriminatorPart`) is a separate `PredicateParts` entry — unaffected, still counted.
- Sequential execution + per-op `RowsAffected` + chunk telemetry: unchanged; a 5000-id update is still one op, one statement, one chunk (cost ~2 params + 0 for nulls).
- Small-list SQL must be byte-identical **for null-free lists** (thresholds above all existing test sizes) so current golden-SQL tests pass untouched.

---

## 7. Risks & open questions

1. **SQL Server string-collation conflict** (EF issue #32147): `OPENJSON … WITH ([value] nvarchar…)` uses database-default collation; a column with a custom collation may error on comparison. Mitigation for v1: integration-test default collation; document limitation; possible follow-up is appending `COLLATE <column-collation>` — requires reading the model collation, do not guess in v1.
2. **No compat gate by design.** Baseline is compat 150+; the generator does not resolve or branch on compatibility level for `IN`, and there is no OR-split fallback. An ancient server (< 130, pre-2016) fails inside SQL Server with its own `OPENJSON` error — accepted and documented, not detected client-side.
3. **SQLite JSON1 availability**: bundled SQLite always has it; still add one integration probe (`SELECT count(*) FROM json_each('[1,2]')`) so an exotic native build fails loudly with a clear message.
4. **PG array inference for exotic types** (`DateOnly`, `TimeOnly`, decimals, value-converted enums, nullable-element arrays): covered by building the typed (possibly nullable) array from converted values + integration tests per type; explicit `NpgsqlDbType` only if inference fails. `byte[]` elements excluded from all fast paths in v1 (§4.2).
5. **Threshold tuning**: 100/50/50 are starting marks from EF10 reasoning, not measurements. Benchmark `IN` vs fast path at 10/50/100/500/5000 rows on each provider before locking them.
6. **`MaxParametersPerCommand` semantics**: after this change the budget still caps everything *except* large-`IN` contents (which cost 1 param + 0 for nulls). Document that; do not silently ignore the budget elsewhere.

---

## 8. Test plan (must all pass before merge)

- **Unit golden-SQL (per provider):**
  - Small list (3 ids) → unchanged `IN (@p0, @p1, @p2)`.
  - 101 ids (SQL Server) → `OPENJSON(@pN) WITH ([value] int …)` + `Parameters.Count == assignments + 1 + discriminator`.
  - 51 ids (SQLite) → `json_each(@pN)`; PG → `= ANY (@pN)`.
  - Overflow backstop: `Threshold = int.MaxValue` + 5000 ids + budget 2000 → still fast path, 1 param, no throw (proves the backstop, not just the threshold). Repeat against SQLite's 999 clamp.
  - Null parity (per provider, small and large): `[1, null]` emits the null as `@p NULL` / JSON `null` / array `NULL` with no `IS NULL` branch; `NULL` rows never match on any path; slow-path and fast-path results agree with each other and with EF Core. Negations keep the `NOT (…)` wrapper.
  - JSON escaping/culture regression: strings with quotes/backslashes/unicode/control chars, decimals under a non-`en` OS locale, `DateTime`/`Guid` round-trip through `OPENJSON WITH` / `json_each` (proves `System.Text.Json` over manual building).
  - Counting consistency: `chunk.Parameters.Count` equals the emitter's actual param count for every golden case.
- **Integration (per provider, real DB):**
  - Seed 6000 rows; `Update … Where(ids5000.Contains(x.Id)).Set(…)` → 5000 rows affected, verified by re-query; same for `Delete`; same for string/Guid id lists.
  - Nullable-column null parity: seed rows with `NULL` in the filtered column; list `[1, null]` matches the `1`-rows and never the `NULL`-rows on every provider and on both paths; results agree with the equivalent EF Core `Where(ids.Contains…)` query.
  - Upsert guard containing a large `IN` (regression for `CountParameterNodes(spec.Guard)` path).
  - SQLite `json_each` probe + PG array-type matrix (int/long/string/Guid/DateTime, plus nullable-element arrays e.g. `int?[]`) + SQL Server `NVARCHAR(MAX)` JSON > 4000 chars (proves no truncation).
- **Perf smoke:** time 5000-id update before (throws / N-param) vs after (1-param) on each provider; plus a 50k-id PG `= ANY` run (asserts 1 param, succeeds, timed) to prove the 50k case stays single-statement; record in PR. If 50k+ lists become routine, evaluate the `DESIGN.md` Strategy B staging-table path separately — out of scope for v1.
- **Existing suites green:** all `ChunkingTests` / `SqliteChunkingTests` / `NpgsqlChunkingTests` throw-tests still pass (they use tiny ops, unaffected); full unit + integration suites.

---

## 9. Rollout order

1. Core helper + JSON/array builders with unit tests (no generator wiring).
2. SQL Server generator + executor + tests (highest user pain: 2100 cap).
3. PostgreSQL generator + tests.
4. SQLite generator + tests.
5. Threshold benchmark → lock marks → docs (`README`, `DESIGN`) → PR with perf numbers.
