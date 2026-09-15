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

## 2. What EF Core 10 does (executable ground truth — observed, not quoted)

This repo pins EF Core 10.0.12 + Npgsql 10.0.3 (`Directory.Packages.props:9-12`; verified on disk: `Microsoft.EntityFrameworkCore.SqlServer.dll` = `10.0.12`, `Npgsql.EntityFrameworkCore.PostgreSQL.dll` = `10.0.3+5e912bf`). Instead of trusting old blogs, we ran that exact build and captured `ToQueryString()` for small / padded / 5000-id / null / `EF.Parameter` cases on all three providers (probe projects under `%TEMP%\opencode\ef10probe*`; full verbatim table in §10). Summary:

- **SQL Server — default: `IN (@p…)` small (with padding: 8 values → 10 params, last value duplicated), auto-`OPENJSON` large:**
  ```sql
  -- 3 ids (default):
  WHERE [b].[Id] IN (@ids31, @ids32, @ids33)
  -- 5000 ids (default): single nvarchar(max) JSON param, auto-switched past the cap:
  -- DECLARE @ids5000 nvarchar(max) = N'[1,2,…,5000]'
  WHERE [b].[Id] IN (
      SELECT [__openjson0].[Value]
      FROM OPENJSON(@ids5000) WITH ([Value] int '$') AS [__openjson0]
  )
  -- EF.Parameter(ids) at ANY size: same OPENJSON shape (ids3 → nvarchar(4000) JSON)
  ```
  Secondary docs: EF8 breaking-change notes + EF8 Preview 4 blog; EF10 keeps `OPENJSON` as the overflow strategy (dotnet/efcore#32394: *"automatically switch to OPENJSON when needed"*; EF10 "Improved translation for parameterized collection"). Requires compat 130+; baseline here is 150+, no compat gate (§4.2). Collation pitfall for custom-collation string columns (dotnet/efcore#32147) stays a §7 risk.
- **PostgreSQL (Npgsql) — `= ANY (@array)` at EVERY size (3, 8 and 5000 observed identical shape):**
  ```sql
  -- @ids3 / @ids5000 as a single array-typed parameter (binary array encoding):
  WHERE b."Id" = ANY (@ids3)
  ```
  How Npgsql does it: the .NET list is sent as **one array-typed parameter**, server compares with `ScalarArrayOp = ANY`. This is Npgsql's documented translation — *"arrayNonColumn.Contains(element) → element = ANY(arrayNonColumn) — Can use regular index"* (npgsql.org `efcore/mapping/array.html`, §2.4-S1) — and Microsoft's EF blog attributes it to the provider: *"we pass the array … directly to ANY"* (EF8 Preview 4, §2.4-S2). Complex compositions use `unnest(@p)` (Npgsql EF 8.0 notes, §2.4-S3); plain `Contains` is `= ANY`. The provider authors choosing `ANY` even for 3-element lists is the strongest available endorsement that it is the optimal single-statement shape — there is no faster per-size alternative to switch between, so **PG gets always-`ANY`, no threshold** (§3).
  Scale math: the 65535 protocol cap counts **parameters, not array elements** — 5000 ids = 1 param, 50000 ids = still 1 param (~200 KB binary for 50k ints, far under the 1 GB field limit). The only shape that can beat it past ~tens of thousands of ids is a temp staging table (`COPY` + `ANALYZE` + `JOIN`, planner statistics) — extra round trips + DDL, so it belongs to the existing `DESIGN.md` Strategy B path, not v1.
- **SQLite — default: `IN (@p…)` at every size (8 → padded to 10, 1500 ids → 1500 params, NO auto-switch observed); `EF.Parameter` → `json_each`:**
  ```sql
  -- EF.Parameter(ids3): single TEXT JSON param:
  -- .param set @ids3 '[1,2,3]'
  WHERE "b"."Id" IN (
      SELECT "i"."value"
      FROM json_each(@ids3) AS "i"
  )
  ```
  Why `json_each` is the right single-param shape (not our invention): the variable cap `SQLITE_MAX_VARIABLE_NUMBER` *"defaults to 999 for SQLite versions prior to 3.32.0 (2020-05-22) or 32766 for SQLite versions after 3.32.0"* (sqlite.org `limits.html`, §2.4-S6) — EF10's default path emits N params and relies on the modern 32766 engine limit, but our generator conservatively clamps to 999, so the overflow backstop (§3) routes large lists to `json_each` where EF10 itself would emit 1500 params. `json_each` is one of only *"two table-valued functions that can be used to decompose a JSON string"*, with output column `value ANY` holding *"INTEGER, REAL, or TEXT depending on the type of the corresponding JSON field"* — integers compare as integers, no cast (sqlite.org `json1.html`, §2.4-S7). It is the direct SQLite analog of `OPENJSON`, and EF10's own framing is exactly this: the JSON array is *"unpacked using the SQL Server OPENJSON function (other databases use similar mechanisms)"* (EF10 "What's New", §2.4-S4).
  Honesty note: no vendor publishes switch-at-N numbers — the claim here is narrower and sourced: above the variable cap a single-param shape is mandatory, and `json_each` is the only in-engine one; below the cap we keep plain `IN` (matching EF10's multi-param default) and our own benchmark (§8) locks the exact switch mark.

Decision: mirror EF10 — **`IN`-small + `OPENJSON`-large (SQL Server) / always-`ANY` (PG) / `IN`-small + `json_each`-large (SQLite)**. Full verbatim evidence table: §10.

### 2.4 Sources (all verified; plan claims trace to these)

- S0 — Executable ground truth (strongest source in this plan): `ToQueryString()` captured from the pinned builds — EFCore `10.0.12`, Npgsql provider `10.0.3+5e912bf` (DLL product versions read off disk) — for 3 / 8 / 1500 / 5000-element `Contains`, null/all-null/negated variants, and `EF.Parameter`-forced single-param form, on all three providers without connecting (verbatim table: §10).

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

Null counting: nulls are stripped before counting (§4.1) — they cost 0 params on either path and never affect the threshold/backstop decision, which sees `nonNullCount` only.

Proposed default marks — **starting values, not claimed optima.** No vendor publishes "switch at N" numbers; the hybrid shape itself is sourced (S8/S0: EF regressed small queries with always-`OPENJSON` and moved the default back to `IN` with auto-`OPENJSON` past the cap), while the exact marks below are our initial settings to be locked by the benchmark in §8 (measure `IN` vs fast path at 10/50/100/500/5000 on each provider; keep whichever wins per size):

| Provider | Proposed `Threshold` | Sourced rationale |
|---|---|---|
| SQL Server | `100` values | S8/S0 justify hybrid over always-`OPENJSON`; below ~100, per-value params are cheap and cardinality-accurate, above they bloat text/plans toward the 2100 cap. Overflow backstop guarantees cap compliance regardless of where the mark lands. |
| SQLite | `50` values | S6 forces single-param above a few hundred ids regardless; S4's multi-param default keeps small `IN` for plan quality below. 50 is early because our 999 clamp is conservative (modern engines allow 32766, but we do not probe) and `json_each` parse is cheap — benchmark confirms. |
| PostgreSQL | none — always `= ANY` | S0: Npgsql emits `= ANY` even for 3 ids; S1: index-capable. No threshold, no small-`IN` path. Consequence: existing Npgsql golden tests that assert small `IN (@p…)` must be updated to `= ANY` (listed in §8). |

**SQLite 999 hard limit — no issue after this change.** `effectiveLimit = min(user, 999)` stays as-is (safe default; newer SQLite builds allow 32766 but we do not probe for it in v1). It cannot break large lists because the decision runs *before* the overflow check: a 5000-id `IN` becomes 1 param (`json_each`) so the op costs ~2 params total, far under 999. Small lists (≤50) still cost N params and are still budget-checked exactly as today — e.g. 40 ids + 1 SET + 1 discriminator = 42 ≤ 999, one chunk; a hypothetical 900-id list with `Threshold = int.MaxValue` still hits the backstop (`900 + other > 999` → fast path → 1 param). The clamp is therefore never a correctness problem, only a (rarely reached) trigger for the fast path.

No new public option in v1: keep the mark as `internal const` per generator plus the automatic overflow backstop. If users later ask for control (mirroring EF's `UseParameterizedCollectionMode` / `EF.Parameter` / `EF.Constant`), add `BulkExecuteOptions.LargeListThreshold` then — not now, to avoid API bloat before measurements.

Consequence for counting: `CountParameterNodes(SqlInNode)` can no longer be `Values.Count` unconditionally. It must return `1` when the fast path will be taken and `Values.Count` otherwise (null elements count as values on the slow path, exactly as today) — using the **same predicate** the emitter uses, or chunk plans and emitted params will disagree.

---

## 4. Detailed design per provider

### 4.1 Shared: EF10-exact null rules + decision function + cost accounting

How EF Core 10 handles nulls in `Contains` (S0 — observed verbatim, §10; this supersedes the earlier "never match" draft, which was wrong — EF10 compensates nulls precisely):

- **C# expressibility constraint (found while probing):** `List<int>.Contains(nullableMember)` does not compile, so nulls in the list imply a **nullable element type**, and an `IS NULL` branch additionally requires a **nullable column**. The 5000-ids-against-PK scenario (`List<int>` vs non-nullable PK) can never contain nulls on either side.
- **SQL Server / SQLite (S0: value-based, static):** nulls are **stripped from the payload** (scalar path: no `@p NULL` param; `OPENJSON`/`json_each` path: JSON without nulls — EF names the param `<name>_without_nulls`), plus a static branch iff `hasNull && columnNullable`:
  - positive: `(IN… OR col IS NULL)` — observed `WHERE [b].[N] IS NULL OR [b].[N] = @p`, `IN (SELECT … OPENJSON(@p_without_nulls) …) OR [b].[N] IS NULL`, `IN (SELECT … json_each(@p_without_nulls) …) OR "N" IS NULL`;
  - negated: `NOT IN… AND col IS NOT NULL` when the list had null — observed `IS NOT NULL AND <>`, `NOT IN (SELECT … OPENJSON …) AND IS NOT NULL`, `NOT IN (SELECT … json_each …) AND IS NOT NULL`;
  - negated with NO null in list but nullable column: `NOT IN… OR col IS NULL` (NULL row is "not in" a null-free list → included) — observed for both scalar and 5000-`OPENJSON`;
  - `hasNull && !columnNullable`: nulls dropped silently, no branch (observed `WHERE [b].[Id] = @nb1`);
  - all-null list: `col IS NULL` / negated `col IS NOT NULL`, zero params.
- **Npgsql (S0: type-based, runtime-checked):** nulls are **kept in the array**, branch iff **element-type-nullable && column-nullable**, decided statically but checked at runtime so one SQL shape serves any values:
  - positive: `col = ANY (@p) OR (col IS NULL AND array_position(@p, NULL) IS NOT NULL)`;
  - negated: `NOT (col = ANY (@p) AND col = ANY (@p) IS NOT NULL) AND (col IS NOT NULL OR array_position(@p, NULL) IS NULL)`;
  - otherwise (incl. nullable-element list vs non-nullable column): plain `col = ANY (@p)` (kept `NULL` array elements are harmless there).
- **Our implementation — static-uniform, provably equivalent:** we generate SQL per execution with materialized values, so `hasNull` is statically known and the runtime `array_position` check can be folded at generation time. Rule for ALL three providers: strip nulls from every payload (params / JSON / array); iff `hasNull && columnNullable` (`IProperty.IsNullable`) emit the positive `(FAST OR col IS NULL)` / negated (`NOT FAST AND col IS NOT NULL`, plus `NOT FAST OR col IS NULL` for negated-no-null-nullable) branches with EF10's distributed shapes above; iff `hasNull && !columnNullable` drop nulls silently. Case-by-case result equivalence against every S0 row was verified by hand in review (including the negated-`ANY` expansion); §8 locks it with differential tests that run each null case on both paths and against EF Core itself. This keeps one rule, avoids nullable-array inference (`int?[]`) entirely, and produces identical result sets to EF10 for the executed values.

Decision helper (core assembly, provider-neutral; SQL Server/SQLite only — PG has no threshold):

```csharp
internal static bool UseFastInPath(int nonNullCount, int effectiveLimit, int otherParamsInOp, int threshold)
    => nonNullCount > threshold
    || nonNullCount + otherParamsInOp > effectiveLimit;   // overflow backstop
```

- `effectiveLimit`: SQL Server = `MaxParametersPerCommand`; SQLite = `min(user, 999)`.
- `otherParamsInOp`: assignments + other predicate parts + discriminator (already counted today). The backstop needs the op-level total, so compute op cost in two passes — first sum non-`IN` params, then decide each `IN` node. A single `IN` of 5000 ids with 1 assignment: `other = 1`, `5000 + 1 > 2000` → fast path → op cost becomes `1 + 1 = 2` (nulls stripped pre-count: a 5000+null list costs the same 1 param + `OR IS NULL` with 0 params).
- Our negation is already `SqlNotNode(SqlInNode)` (translator wraps `!Contains`; `SqlInNode.Negated` is never set). Emitters must produce EF10's **distributed** negated shapes (`NOT IN… AND IS NOT NULL` / `NOT IN… OR IS NULL`), NOT a `NOT (…)` wrapper — the wrapper is wrong for nullable columns (it excludes `NULL` rows EF includes, and includes rows EF excludes when the list had null). `SqlInNode.Negated` stays unused.
- Empty list: unchanged — `IN` → `1=0`, `NOT IN` → `NOT (1=0)`-equivalent (`1=1`), zero params. Fast path never triggers for 0 values. All-null list is distinct from empty: `col IS NULL` / `col IS NOT NULL`, zero list params (S0).

### 4.2 SQL Server — `OPENJSON`

Locked decision per review: **no compatibility-level check, no fallback.** Baseline is compat 150+ (`OPENJSON` exists since 130 / SQL Server 2016), so the generator emits `OPENJSON` unconditionally for large lists. On an ancient database the server itself throws — that is the intended behavior, not a library fallback path. (`SqlServerProvider`'s compat resolution stays untouched for the existing `LEAST`/`GREATEST` gate; it is not consulted for `IN`.)

Small (`valueCount ≤ threshold`, fits budget) — null-free lists byte-identical to today; null rules per §4.1 (S0 shapes):

```sql
[Id] IN (@p0, @p1, @p2)                                            -- no nulls (any column)
([Id] IN (@p0) OR [Id] IS NULL)                                    -- hasNull && nullable col
[Id] NOT IN (@p0, @p1) OR [Id] IS NULL                             -- negated, no nulls, nullable col
[Id] NOT IN (@p0) AND [Id] IS NOT NULL                             -- negated, hasNull && nullable col
[Id] IS NULL / [Id] IS NOT NULL                                    -- all-null list (0 params)
```

Large — one `NVARCHAR(MAX)` JSON param of the **non-null** values (S0: `@p = N'[1,2,…,5000]'`):

```sql
[Id] IN (SELECT [v].[Value] FROM OPENJSON(@p7) WITH ([Value] int '$') AS [v])
([Id] IN (SELECT …) OR [Id] IS NULL)                               -- hasNull && nullable col
[Id] NOT IN (SELECT …) AND [Id] IS NOT NULL                        -- negated, hasNull && nullable col
[Id] NOT IN (SELECT …) OR [Id] IS NULL                             -- negated, no nulls, nullable col
```

Deliberate deviation from S0 (documented, results-identical): EF omits the `WITH` clause when the values contained null (default schema + implicit conversion). We **always** emit `WITH ([Value] <type> '$')` from the EF column/store type because the column type is known at bind time — avoids the implicit conversion and keeps the predicate sargable. Not mirrored from EF (also not mirrored): padding (8→10 — pointless for per-execution SQL with no plan reuse), single-value `=` rewrite, inline constants in negated small `IN`, `nvarchar(4000)`-vs-`max` param sizing (we always send `NVARCHAR(MAX)`-capable params; §5 item 7), and EF's `@p_without_nulls` naming (ours keep chunk `@pN` naming).

Type map for the `WITH ([Value] <type> '$')` clause, derived from EF metadata (`IProperty.GetColumnType()` / relational type mapping; fallback by CLR type of the **provider-converted** values):

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

JSON building: serialize the **provider-converted, non-null** values (`SqlInNode.Values` are already converted at translate time; nulls stripped per §4.1) with **`System.Text.Json` — not a hand-rolled `StringBuilder`**. Correctness review decision: manual JSON is rejected for this path. Data can be any mapped type (strings with quotes/backslashes/control chars, decimals where the OS locale uses `,` as separator, `DateTime`/`DateOnly`/`Guid`, etc.) — a simple escaper will break on some of it, and a fully-correct manual writer just re-implements `System.Text.Json` worse. `System.Text.Json` is the same serializer EF Core itself relies on across its JSON support, is culture-invariant by default (numbers/dates serialize per JSON spec, never per OS locale), handles all CLR primitives including `DateOnly`/`TimeOnly` on .NET 10, and is itself heavily optimized (UTF-8, pooled buffers, SIMD) — a manual builder has no meaningful performance edge once the dominant costs (TDS round-trip + `OPENJSON` parse) are counted, and it carries all of the escaping risk. Concretely: `JsonSerializer.Serialize` over the non-null values (typed `List<T>` overload where the element type is known, else `IReadOnlyList<object?>`) with default options; null never reaches the serializer (stripped per §4.1). S0 reference: EF sends `@p = N'[1,2,…,5000]'` (`nvarchar(max)` beyond 4000 chars, `nvarchar(4000)` for `'[1,2,3]'`). Known edge: `byte[]` **elements** (i.e. `List<byte[]>` against a `varbinary` column) — `System.Text.Json` serializes each `byte[]` as a base64 string, which does not round-trip through `WITH ([value] varbinary…)` without extra work. Rare path: keep `byte[]`-element lists on the multi-param `IN` route in v1; if such an op would overflow the budget, throw the existing overflow error with guidance (acceptable for v1; revisit only if requested).
Param sending: value is a .NET `string`; executors today do `dbParam.Value = value` with no `DbType`. For >4000-char JSON this must be `NVARCHAR(MAX)`: set `SqlDbType.NVarChar, Size = -1` when the connection is `Microsoft.Data.SqlClient`-backed (detect via parameter type name, no new package ref — core stays provider-neutral; do it in `SqlServerExecutor` which already lives in the SqlServer package). Verify `Microsoft.Data.SqlClient` infers MAX correctly without the hint; add the hint only if tests show truncation at 4000.

### 4.3 PostgreSQL — always `= ANY (@p)` (no small/large split, S0)

Every size uses one typed array param of the **non-null** values (S0: `@p` sent as array, `= ANY` for 3, 8 and 5000 alike):

```sql
"Id" = ANY (@p7)                                               -- no nulls (any column)
("Id" = ANY (@p7) OR "Id" IS NULL)                             -- hasNull && nullable col
"Id" = ANY (@p7)                                               -- hasNull && non-null col (nulls dropped)
"Id" NOT IN (…) → distributed: see below
"Id" IS NULL / "Id" IS NOT NULL                                -- all-null list (0 params)
```

Negated (distributed EF10-equivalent static shapes — §4.1 proves result-equivalence with EF's runtime `array_position` expansion for the executed values; do NOT use a `NOT (…)` wrapper and do NOT rewrite to `<> ALL`):

```sql
("Id" = ANY (@p7) in negated form):
NOT ("Id" = ANY (@p7)) AND "Id" IS NOT NULL                    -- negated, hasNull && nullable col
NOT ("Id" = ANY (@p7)) OR "Id" IS NULL                         -- negated, no nulls, nullable col
NOT ("Id" = ANY (@p7))                                         -- negated, non-null col
```

Array building: `SqlInNode.Values` are provider-converted `object?` — strip nulls first (§4.1), then build a **typed non-nullable CLR array** of the element type (int→`int[]`, long→`long[]`, string→`string[]`, Guid→`Guid[]`, …; all-null → no array at all). Deriving the element type: first non-null value's runtime type, falling back to the property CLR type (unwrapping nullable/enum → underlying). Npgsql infers the PG array type from the CLR array — no `NpgsqlDbType` reference needed in code (avoid a new compile dependency); verify with integration tests that `int[]`, `long[]`, `string[]`, `Guid[]`, `DateTime[]` bind correctly through `connection.CreateCommand()` (the executors use provider-agnostic `CreateParameter`, which returns `NpgsqlParameter` on a PG connection — inference happens there). If inference fails for any type (e.g. enums pre-conversion — shouldn't happen since values are converted), set the type via reflection-free duck-typing in `NpgsqlExecutor` (already lives in the Npgsql package, so referencing `NpgsqlTypes` there is acceptable if needed).
Empty (no values at all): `1=0` / `1=1` as today.
`unnest` is NOT needed for plain `Contains` (S0 confirms Npgsql uses `ANY` for it); reserve it for future composed operators (`Any` over lists — out of scope).

### 4.4 SQLite — `json_each` (S0 shape)

Small — null-free lists byte-identical to today; null rules per §4.1 (S0 shapes):

```sql
"Id" IN (@p0, @p1)                                             -- no nulls (any column)
("Id" IN (@p0) OR "Id" IS NULL)                                -- hasNull && nullable col
"Id" NOT IN (@p0, @p1) OR "Id" IS NULL                         -- negated, no nulls, nullable col
"Id" NOT IN (@p0) AND "Id" IS NOT NULL                         -- negated, hasNull && nullable col
"Id" IS NULL / "Id" IS NOT NULL                                -- all-null list (0 params)
```

Large — one JSON `TEXT` param of the **non-null** values (S0: `@p = '[1,2,3]'`), serialized with `System.Text.Json` (same rationale as §4.2 — no manual JSON):

```sql
"Id" IN (SELECT "i"."value" FROM json_each(@p7) AS "i")       -- @p7 = '[1,2,3]'
("Id" IN (SELECT …) OR "Id" IS NULL)                           -- hasNull && nullable col
"Id" NOT IN (SELECT …) AND "Id" IS NOT NULL                    -- negated, hasNull && nullable col
"Id" NOT IN (SELECT …) OR "Id" IS NULL                         -- negated, no nulls, nullable col
```

Notes: `json_each(@p)` with a JSON array binds the array via the parameter — no string interpolation. `value` column carries JSON values with native affinity (integers compare as integers). JSON1 is compiled into `Microsoft.Data.Sqlite`'s bundled SQLite on all supported platforms; add one integration probe (`SELECT count(*) FROM json_each('[1,2]')`) so an exotic native build fails loudly with a clear message. Same `byte[]`-element exception as SQL Server: keep binary-element lists on the multi-param route in v1.

---

## 5. Files to touch (implementation checklist)

1. `Internal/SqlNodes.cs` — no shape change needed (keep `SqlInNode(Property, Values)` — values include nulls; `Negated` stays unused). No element-type field needed: the static-uniform rule (§4.1) only needs `hasNull` (from `Values`) + `IProperty.IsNullable` at emit time.
2. `Internal/LinqPredicateTranslator.cs` — **no change** (still materializes `Values`, nulls included; conversion stays here).
3. **New** `Internal/LargeListHelper.cs` (core, provider-neutral): SQL Server/SQLite `Threshold` consts + `ShouldUseFastPath(...)` (non-null count + backstop), null-strip helper (`PartitionNulls` → `(nonNulls, hasNull)`), JSON payload builder via `System.Text.Json` over converted **non-nulls**, typed non-nullable array builder for PG (`Array BuildTypedArray(IProperty, nonNulls)`; `byte[]` elements excluded → caller keeps multi-param route — for PG that means plain `IN (@p…)` with normal budget checks).
4. `SqlServerSqlGenerator.cs` — `CountParameterNodes` + `EmitIn` gain fast path + EF10 null branches (§4.1–4.2, distributed negated shapes — the `SqlNotNode(SqlInNode)` emitter case must distribute, not wrap); `Generate`'s single-op overflow check (`:31-37`) must use the **fast-aware** cost (nulls stripped pre-count) or a 5000-id op still throws before emitting. Same for the upsert-guard path (`ExpandUpsert` fixed-cost calc uses `CountParameterNodes(spec.Guard)` — automatically fixed once counting is fast-aware). No compat-level wiring for `IN`.
5. `SqliteSqlGenerator.cs` — same two functions (§4.1 + §4.4); overflow check `:30-34` uses the fast-aware cost (this is what makes the 999 clamp harmless for large lists).
6. `NpgsqlSqlGenerator.cs` — `EmitIn` becomes always-`ANY` + EF10-equivalent static null branches (§4.1 + §4.3); `CountParameterNodes(SqlInNode)` becomes constant `1` (plus 0-param all-null `IS NULL` case); overflow check `:30-35` stays but can never fire for `IN` (single array param). Handle `EmitQualified` too (upsert-guard path uses `EmitQualified`; `ANY` + `IS NULL` branches must qualify the column there).
7. `SqlServerExecutor.cs` — ensure JSON param goes as `NVARCHAR(MAX)` (test; add `SqlDbType` hint only if needed).
8. `NpgsqlExecutor.cs` — verify array inference; add explicit typing only if a type fails.
9. `SqliteExecutor.cs` — no change expected (TEXT param as today).
10. Docs: `README.md` (one line under batching: large `IN` lists use single-param fast path) + `docs/DESIGN.md` §4 chunking paragraph (replace "~2100 params/request, 4 params → ~500 ops" example with large-`IN` note).

---

## 6. Semantics — EF10-exact (S0), including the breaking bits called out

- Negation uses EF10's **distributed** shapes (`NOT IN… AND IS NOT NULL` / `NOT IN… OR IS NULL`), never a `NOT (…)` wrapper: the wrapper is wrong for nullable columns in both directions (S0 §10). `SqlInNode.Negated` stays unused; the `SqlNotNode(SqlInNode)` emitter case distributes per §4.1.
- **Nulls follow §4.1 exactly** (strip + static branch iff `hasNull && columnNullable`; drop silently iff `hasNull && !columnNullable`; all-null → `IS NULL`/`IS NOT NULL` with 0 params; empty → `1=0`/`1=1`). This changes today's behavior in the nullable edge (today nulls never match and negations misbehave) — intentionally, to match EF10 result sets.
- **Known SQL breaks vs today (all EF10-aligned):** (a) Npgsql small lists change from `IN (@p…)` to `= ANY (@p)` — Npgsql golden tests asserting small `IN` must be updated (§8); (b) nullable-column `Contains` gains `OR IS NULL` / `AND IS NOT NULL` branches; (c) `NOT (IN…)` wrapper disappears for `Contains`-negations. Non-nullable, null-free filters (the 5000-ids-against-PK scenario) are byte-identical on SQL Server/SQLite small path.
- Value converters / enums: values are converted at translate time (`ConvertToProvider`); JSON/array builders consume the **converted non-null** values, never re-convert. `System.Text.Json` input is therefore plain primitives.
- Discriminator filter (`ModelBinder.AddDiscriminatorPart`) is a separate `PredicateParts` entry — unaffected, still counted.
- Sequential execution + per-op `RowsAffected` + chunk telemetry: unchanged; a 5000-id update is still one op, one statement, one chunk (SQL Server/SQLite cost ~2 params; PG cost 1 array param).
- Deliberate EF10 non-mirrors (no result impact, documented in §4.2): no padding, no single-value `=` rewrite, no inline constants, always-`WITH`, always-`NVARCHAR(MAX)`-capable JSON params, own `@pN` naming.

---

## 7. Risks & open questions

1. **SQL Server string-collation conflict** (EF issue #32147): `OPENJSON … WITH ([value] nvarchar…)` uses database-default collation; a column with a custom collation may error on comparison. Mitigation for v1: integration-test default collation; document limitation; possible follow-up is appending `COLLATE <column-collation>` — requires reading the model collation, do not guess in v1.
2. **No compat gate by design.** Baseline is compat 150+; the generator does not resolve or branch on compatibility level for `IN`, and there is no OR-split fallback. An ancient server (< 130, pre-2016) fails inside SQL Server with its own `OPENJSON` error — accepted and documented, not detected client-side.
3. **SQLite JSON1 availability**: bundled SQLite always has it; still add one integration probe (`SELECT count(*) FROM json_each('[1,2]')`) so an exotic native build fails loudly with a clear message.
4. **PG array inference for exotic types** (`DateOnly`, `TimeOnly`, decimals, value-converted enums): covered by building the typed non-nullable array from converted non-null values + integration tests per type; explicit `NpgsqlDbType` only if inference fails. `byte[]` elements excluded from the fast path in v1 (§4.2).
5. **Threshold tuning**: 100 (SQL Server) / 50 (SQLite) are starting marks from EF10 reasoning, not measurements. Benchmark `IN` vs fast path at 10/50/100/500/5000 rows on each provider before locking them. PG needs no threshold (always-`ANY`, S0).
6. **`MaxParametersPerCommand` semantics**: after this change the budget still caps everything *except* large-`IN` contents (which cost 1 param + 0 for nulls). Document that; do not silently ignore the budget elsewhere.

---

## 8. Test plan (must all pass before merge)

- **Unit golden-SQL:**
  - SQL Server / SQLite small null-free (3 ids) → unchanged `IN (@p0, @p1, @p2)`.
  - SQL Server 101 ids → `OPENJSON(@pN) WITH ([Value] <type> …)` + `Parameters.Count == assignments + 1 + discriminator`.
  - SQLite 51 ids → `json_each(@pN)` single TEXT param.
  - Npgsql any size (3 and 5000) → `"Col" = ANY (@pN)` single array param; **update existing Npgsql goldens that assert small `IN`** to `= ANY`.
  - Overflow backstop: `Threshold = int.MaxValue` + 5000 ids + budget 2000 → still fast path, 1 param, no throw (proves the backstop, not just the threshold). Repeat against SQLite's 999 clamp.
  - Null matrix, exact S0 shapes (§10) per provider, small AND large: `[1, null]` vs nullable → `(IN… OR IS NULL)`; vs non-null col → null dropped, plain `IN`; `[null]`-only → `IS NULL` (0 params); `[]` → `1=0`; negated `[1, null]` vs nullable → `NOT IN… AND IS NOT NULL`; negated no-null vs nullable → `NOT IN… OR IS NULL`; PG static shapes asserted param-for-param (array holds non-nulls only).
  - Equivalence: every null-matrix case run on both sides of the threshold gives identical result sets (slow `IN` ≡ fast `OPENJSON`/`json_each`/`ANY`); spot-check against EF Core `ToQueryString` shapes from §10.
  - JSON escaping/culture regression: strings with quotes/backslashes/unicode/control chars, decimals under a non-`en` OS locale, `DateTime`/`Guid` round-trip through `OPENJSON WITH` / `json_each` (proves `System.Text.Json` over manual building).
  - Counting consistency: `chunk.Parameters.Count` equals the emitter's actual param count for every golden case.
- **Integration (per provider, real DB):**
  - Seed 6000 rows; `Update … Where(ids5000.Contains(x.Id)).Set(…)` → 5000 rows affected, verified by re-query; same for `Delete`; same for string/Guid id lists.
  - Nullable-column null semantics per §4.1/§10: seed `NULL` + non-`NULL` rows; `[1, null]` matches both the `1`-rows and the `NULL`-rows; `[null]`-only matches exactly the `NULL`-rows; negations invert; results equal the equivalent EF Core query on the same seed.
  - Upsert guard containing a large `IN` (regression for `CountParameterNodes(spec.Guard)` path).
  - SQLite `json_each` probe + PG array-type matrix (int/long/string/Guid/DateTime) + SQL Server `NVARCHAR(MAX)` JSON > 4000 chars (proves no truncation).
- **Perf smoke:** time 5000-id update before (throws / N-param) vs after (1-param) on each provider; plus a 50k-id PG `= ANY` run (asserts 1 param, succeeds, timed) to prove the 50k case stays single-statement; record in PR. If 50k+ lists become routine, evaluate the `DESIGN.md` Strategy B staging-table path separately — out of scope for v1.
- **Existing suites green:** all `ChunkingTests` / `SqliteChunkingTests` / `NpgsqlChunkingTests` throw-tests still pass (they use tiny ops, unaffected); full unit + integration suites.

---

## 9. Rollout order

1. Core helper + JSON/array builders with unit tests (no generator wiring).
2. SQL Server generator + executor + tests (highest user pain: 2100 cap).
3. PostgreSQL generator (always-`ANY` + Npgsql golden updates) + tests.
4. SQLite generator + tests.
5. Threshold benchmark → lock marks → docs (`README`, `DESIGN`) → PR with perf numbers.

---

## 10. Appendix — EF10 ground-truth verbatims (S0)

Method: console probe referencing the pinned builds (EFCore `10.0.12`, Npgsql provider `10.0.3+5e912bf`), `ToQueryString()` only (never connects), entity `Blog { int Id; string Name; int? N; Guid G }`. `N` = nullable column, `Id` = non-nullable column. Whitespace normalized; params shown as EF prints them.

**SQL Server**

| Case | Verbatim SQL |
|---|---|
| 3 ids vs `Id` | `WHERE [b].[Id] IN (@ids31, @ids32, @ids33)` |
| 8 ids vs `Id` | `WHERE [b].[Id] IN (@ids81, …, @ids810)` — 10 params, `@ids89 = @ids810 = 8` (padding) |
| 5000 ids vs `Id` | `DECLARE @ids5000 nvarchar(max) = N'[1,2,…,5000]'` … `WHERE [b].[Id] IN (SELECT [__openjson0].[Value] FROM OPENJSON(@ids5000) WITH ([Value] int '$') AS [__openjson0])` |
| `EF.Parameter(ids)` any size | same `OPENJSON` shape (3 ids → `nvarchar(4000)` JSON `N'[1,2,3]'`, `WITH ([value] int '$')`) |
| `{1, null}` vs `N` | `WHERE [b].[N] IS NULL OR [b].[N] = @nullables1` |
| `!{1, null}` vs `N` | `WHERE [b].[N] IS NOT NULL AND [b].[N] <> @nullables1` |
| `{1, 2}` (`List<int?>`, no nulls) vs `N` | `WHERE [b].[N] IN (@p1, @p2)` (no null branch — value-based) |
| `!{1, 2}` vs `N` | `WHERE [b].[N] NOT IN (@p1, @p2) OR [b].[N] IS NULL` |
| `{1, null}` vs `Id` | `WHERE [b].[Id] = @nb1` (null dropped, single → `=`) |
| 5000+null vs `N` | `DECLARE @bigWithNull_without_nulls nvarchar(max) = N'[1,2,…,5000]'` … `WHERE [b].[N] IN (SELECT [__openjson0].[Value] FROM OPENJSON(@bigWithNull_without_nulls) AS [__openjson0]) OR [b].[N] IS NULL` (null stripped; note: **no `WITH`** — our §4.2 always emits `WITH`, deliberate deviation) |
| `!`(5000+null) vs `N` | `WHERE [b].[N] NOT IN (SELECT … OPENJSON(@…_without_nulls) …) AND [b].[N] IS NOT NULL` |
| 5000 no-nulls (`List<int?>`) vs `N` | `WITH ([Value] int '$')`, no null branch; negated: `NOT IN (SELECT …) OR [b].[N] IS NULL` |
| `{null}` vs `N` | `WHERE [b].[N] IS NULL` (0 params); negated `IS NOT NULL` |

**SQLite** (mirrors SQL Server; `.param set` display)

| Case | Verbatim SQL |
|---|---|
| 3 / 8 ids | `IN (@p…)` (8 → padded to 10, dup last) |
| 1500 ids, default path | 1500 params, **no auto-switch** (relies on modern 32766 engine limit) |
| `EF.Parameter(ids)` | `.param set @ids3 '[1,2,3]'` … `WHERE "b"."Id" IN (SELECT "i"."value" FROM json_each(@ids3) AS "i")` |
| `{1, null}` vs `N` | `WHERE "b"."N" IS NULL OR "b"."N" = @p` |
| `EF.Parameter({1, null})` vs `N` | `.param set @nb_without_nulls '[1]'` … `WHERE "b"."N" IN (SELECT "n"."value" FROM json_each(@nb_without_nulls) AS "n") OR "b"."N" IS NULL` |
| `!EF.Parameter({1, null})` vs `N` | `WHERE "b"."N" NOT IN (SELECT … json_each …) AND "b"."N" IS NOT NULL` |

**Npgsql** (always `= ANY`; `-- @p={…} (DbType = Object)` display)

| Case | Verbatim SQL |
|---|---|
| 3 / 8 / 5000 ids vs `Id` | `WHERE b."Id" = ANY (@p)` — identical shape all sizes |
| `{1, null}` vs `N` | `WHERE b."N" = ANY (@p) OR (b."N" IS NULL AND array_position(@p, NULL) IS NOT NULL)` (array keeps `NULL`; runtime check) |
| `!{1, null}` vs `N` | `WHERE NOT (b."N" = ANY (@p) AND b."N" = ANY (@p) IS NOT NULL) AND (b."N" IS NOT NULL OR array_position(@p, NULL) IS NULL)` |
| `{1, 2}` (`List<int?>`, no nulls) vs `N` | same `OR (… array_position …)` shape (type-based decision — branch present despite no null values) |
| `{1, null}` vs `Id` | `WHERE b."Id" = ANY (@p)` (no branch — non-null column) |
| `{null}` vs `N` | same `OR (… array_position …)` shape over `{ NULL }` |

**C# expressibility constraint (compile-time, verified):** `List<int>.Contains(int? member)` does not compile, so nulls-in-list ⇒ nullable element type; an `IS NULL` branch additionally requires a nullable column. The PK-filter scenario (`List<int>` vs non-nullable key) can never contain nulls.
