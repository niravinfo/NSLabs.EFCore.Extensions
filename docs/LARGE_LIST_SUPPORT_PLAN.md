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
  Source: Npgsql/EF docs ("`arrayNonColumn.Contains(element)` → `element = ANY(arrayNonColumn)`, can use regular index") and EF8 Preview 4 ("we pass the array … directly to ANY"). Complex compositions use `unnest(@p)`; simple `Contains` is `= ANY`. One param, no 65535 pressure, index-friendly.
- **SQLite — `json_each`, single JSON-text param:**
  EF's exact SQLite parameterized-`Contains` SQL is not called out in the docs the way `OPENJSON`/`ANY` are, but the correct fast path on SQLite is the JSON1 table-valued function (always bundled with modern SQLite / `Microsoft.Data.Sqlite`):
  ```sql
  WHERE "Id" IN (SELECT "value" FROM json_each(@p0))   -- @p0 = '[1,2,3]'
  ```
  One `TEXT` param, sidesteps the 999-variable limit entirely. `json_each.value` preserves JSON types (integer stays integer, text stays text), so no cast is needed for the common cases.

Decision: mirror EF — **OPENJSON (SQL Server) / `= ANY` (PG) / `json_each` (SQLite)**.

---

## 3. The "mark" (threshold): hybrid, not always-JSON

EF10's lesson is that small `IN (@p0…)` gives the optimizer better cardinality info than unpacking JSON/arrays, while large lists must avoid the param cap and plan bloat. So:

- **Keep `IN (@p…)` for small lists** (existing golden SQL unchanged, best plan quality).
- **Switch to the single-param fast path when EITHER is true:**
  1. `Values.Count > Threshold` (the "mark"), **OR**
  2. expanding to N params would overflow the op's share of the effective budget (correctness backstop — a 5000-id list must succeed even if someone sets `Threshold = 10000`).

Proposed default marks (tunable after benchmarks, §8):

| Provider | Proposed `Threshold` | Rationale |
|---|---|---|
| SQL Server | `100` values | Below 100, `IN` params are cheap and give better cardinality; above, `OPENJSON` parse cost is dwarfed by avoiding 100+ params and plan variants. Overflow backstop guarantees ≤2100 compliance regardless. |
| SQLite | `50` values | 999 budget is tiny; JSON parse on SQLite is cheap; switch early. |
| PostgreSQL | `50` values (or always-`ANY` — decide in implementation) | `= ANY(array)` is at parity or faster even for small lists and uses a regular index; the only reason to keep small-`IN` is golden-SQL stability. If benchmarks show `ANY` never regresses, simplify PG to always-`ANY`. |

No new public option in v1: keep the mark as `internal const` per generator plus the automatic overflow backstop. If users later ask for control (mirroring EF's `UseParameterizedCollectionMode` / `EF.Parameter` / `EF.Constant`), add `BulkExecuteOptions.LargeListThreshold` then — not now, to avoid API bloat before measurements.

Consequence for counting: `CountParameterNodes(SqlInNode)` can no longer be `Values.Count` unconditionally. It must return `1` when the fast path will be taken, `Values.Count` otherwise — using the **same predicate** the emitter uses, or chunk plans and emitted params will disagree.

---

## 4. Detailed design per provider

### 4.1 Shared: decision function + cost accounting

Add one shared helper (core assembly, provider-neutral on inputs):

```csharp
internal static bool UseFastInPath(int valueCount, int effectiveLimit, int otherParamsInOp, int threshold)
    => valueCount > threshold
    || valueCount + otherParamsInOp > effectiveLimit;   // overflow backstop
```

- `effectiveLimit`: SQL Server = `MaxParametersPerCommand`; SQLite = `min(user, 999)`; PG = `min(user, 65535)`.
- `otherParamsInOp`: assignments + other predicate parts + discriminator (already counted today). The backstop needs the op-level total, so the per-`SqlInNode` count function needs context: easiest is to compute the op cost in two passes — first sum non-`IN` params, then decide each `IN` node. A single `IN` of 5000 with 1 assignment: `other = 1`, `5000 + 1 > 2000` → fast path → op cost becomes `1 + 1 = 2`.
- Negation arrives as `SqlNotNode(SqlInNode)` (translator wraps `!Contains`; `SqlInNode.Negated` is never set today — see §6). The decision function must look through `SqlNotNode` when counting, and the emitter must emit the negated fast form.
- Empty list: unchanged — `IN` → `1=0`, `NOT IN` → `1=1` (via `NOT (1=0)`), zero params. Fast path never triggers for 0 values.

### 4.2 SQL Server — `OPENJSON`

Small (`≤ threshold`, fits budget) — byte-identical to today:

```sql
[Id] IN (@p0, @p1, @p2)
NOT ([Id] IN (@p0, @p1))   -- via SqlNotNode wrapper, as today
```

Large — one `NVARCHAR(MAX)` JSON param:

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

JSON building: serialize the **provider-converted** values (`SqlInNode.Values` are already converted at translate time) to a JSON array string with `StringBuilder` (no `System.Text.Json` per-element overhead for primitives; strings escaped per JSON). `null` elements serialize as `null` (preserves today's `IN (…, NULL)` semantics — `IN (SELECT …)` with a NULL row is still UNKNOWN, never a match).
Param sending: value is a .NET `string`; executors today do `dbParam.Value = value` with no `DbType`. For >4000-char JSON this must be `NVARCHAR(MAX)`: set `SqlDbType.NVarChar, Size = -1` when the connection is `Microsoft.Data.SqlClient`-backed (detect via parameter type name, no new package ref — core stays provider-neutral; do it in `SqlServerExecutor` which already lives in the SqlServer package). Verify `Microsoft.Data.SqlClient` infers MAX correctly without the hint; add the hint only if tests show truncation at 4000.
Compat: `OPENJSON` needs compat ≥ 130. `SqlServerProvider` already resolves the compat level (`SqlServerProvider.cs:16-55`). If level < 130 and a large list arrives: fallback = split the single logical op into multiple `OR`'d `IN` chunks **within one statement** (`WHERE (col IN (…400…) OR col IN (…400…))`), each chunk fitting the budget — or throw a clear "raise compat to 130+ or lower Threshold" error. Prefer the OR-split fallback so it works everywhere; document the slight plan-size cost.

### 4.3 PostgreSQL — `= ANY (@p)`

Small — byte-identical to today:

```sql
"Id" IN (@p0, @p1)
NOT ("Id" IN (@p0, @p1))
```

Large — one typed array param:

```sql
"Id" = ANY (@p7)
NOT ("Id" = ANY (@p7))     -- preserves exact NOT-IN null semantics; do NOT rewrite to <> ALL
```

Array building: `SqlInNode.Values` are provider-converted `object?`. Build a **typed CLR array** of the element type (int→`int[]`, long→`long[]`, string→`string[]`, Guid→`Guid[]`, …). Deriving the element type: use the first non-null value's runtime type, falling back to the property CLR type (unwrapping nullable/enum → underlying). Npgsql infers the PG array type from the CLR array — no `NpgsqlDbType` reference needed in code (avoid a new compile dependency); verify with integration tests that `int[]`, `long[]`, `string[]`, `Guid[]`, `DateTime[]` bind correctly through `connection.CreateCommand()` (the executors use provider-agnostic `CreateParameter`, which returns `NpgsqlParameter` on a PG connection — inference happens there). If inference fails for any type (e.g. enums pre-conversion — shouldn't happen since values are converted), set the type via reflection-free duck-typing in `NpgsqlExecutor` (already lives in the Npgsql package, so referencing `NpgsqlTypes` there is acceptable if needed).
Empty: `1=0` / `NOT (1=0)` as today. Nulls in array: `col = ANY (ARRAY[…,NULL])` matches today's `IN (…,NULL)` semantics (UNKNOWN, no match for non-null cols).
`unnest` is NOT needed for plain `Contains`; reserve it for future composed operators (`Any` over lists — out of scope).

### 4.4 SQLite — `json_each`

Small — byte-identical to today:

```sql
"Id" IN (@p0, @p1)
NOT ("Id" IN (@p0, @p1))
```

Large — one JSON `TEXT` param:

```sql
"Id" IN (SELECT "value" FROM json_each(@p7))         -- @p7 = '[1,2,3]'
NOT ("Id" IN (SELECT "value" FROM json_each(@p7)))
```

Notes: `json_each(@p)` with a JSON array binds the array via the parameter — no string interpolation. `value` column carries JSON values with native affinity (integers compare as integers). Strings escaped per JSON. `NULL` elements → JSON `null` → same UNKNOWN semantics as today. JSON1 is compiled into `Microsoft.Data.Sqlite`'s bundled SQLite on all supported platforms; add a startup integration check, and if `json_each` is ever missing, fall back to OR-split `IN` chunks (same fallback shape as SQL Server compat fallback).

---

## 5. Files to touch (implementation checklist)

1. `Internal/SqlNodes.cs` — no shape change needed (keep `SqlInNode(Property, Values)`; `Negated` stays unused). Optional: add `ElementType` cache — prefer deriving at emit time to avoid translator changes.
2. `Internal/LinqPredicateTranslator.cs` — **no change** (still materializes `Values`; conversion stays here).
3. **New** `Internal/LargeListHelper.cs` (core, provider-neutral): threshold consts or per-provider args, `ShouldUseFastPath(...)`, JSON-array builder (`AppendJsonArray(StringBuilder, IReadOnlyList<object?>)` handling string/Guid/DateTime/bool/numeric/null + provider-converted values), typed-array builder for PG (`Array BuildTypedArray(IProperty, IReadOnlyList<object?>)`).
4. `SqlServerSqlGenerator.cs` — `CountParameterNodes` + `EmitIn` gain fast path (§4.2); `Generate`'s single-op overflow check (`:31-37`) must use the **fast-aware** cost or a 5000-id op still throws before emitting. Same for the upsert-guard path (`ExpandUpsert` fixed-cost calc uses `CountParameterNodes(spec.Guard)` — automatically fixed once counting is fast-aware).
5. `SqliteSqlGenerator.cs` — same two functions (§4.4); overflow check `:30-34`.
6. `NpgsqlSqlGenerator.cs` — same two functions (§4.3); overflow check `:30-35`. Handle `EmitQualified` too (upsert-guard path uses `EmitQualified`; `IN`/`ANY` emission must qualify the column there).
7. `SqlServerExecutor.cs` — ensure JSON param goes as `NVARCHAR(MAX)` (test; add `SqlDbType` hint only if needed).
8. `NpgsqlExecutor.cs` — verify array inference; add explicit typing only if a type fails.
9. `SqliteExecutor.cs` — no change expected (TEXT param as today).
10. Docs: `README.md` (one line under batching: large `IN` lists use single-param fast path) + `docs/DESIGN.md` §4 chunking paragraph (replace "~2100 params/request, 4 params → ~500 ops" example with large-`IN` note).

---

## 6. Semantics that must NOT change (or tests will catch it)

- `!ids.Contains(x.Id)` stays `NOT (… IN …)` — today via `SqlNotNode(SqlInNode)` (`LinqPredicateTranslator.cs:20-21` + `Emit … SqlNotNode not => $"NOT ({…})"` in all three emitters). Fast path keeps the `NOT (...)` wrapper; never rewrite to `NOT IN`/`<> ALL` with different NULL behavior.
- Empty list: `EmitIn` early-returns `1=0` / `1=1` (all three generators) — keep, zero params.
- Value converters / enums: values are converted at translate time (`ConvertToProvider`); JSON/array builders must consume the **converted** values, never re-convert.
- Discriminator filter (`ModelBinder.AddDiscriminatorPart`) is a separate `PredicateParts` entry — unaffected, still counted.
- Sequential execution + per-op `RowsAffected` + chunk telemetry: unchanged; a 5000-id update is still one op, one statement, one chunk (cost ~2 params).
- Small-list SQL must be byte-identical (thresholds above all existing test sizes) so current golden-SQL tests pass untouched.

---

## 7. Risks & open questions

1. **SQL Server string-collation conflict** (EF issue #32147): `OPENJSON … WITH ([value] nvarchar…)` uses database-default collation; a column with a custom collation may error on comparison. Mitigation for v1: integration-test default collation; document limitation; possible follow-up is appending `COLLATE <column-collation>` — requires reading the model collation, do not guess in v1.
2. **SQL Server compat < 130**: needs the OR-split fallback (§4.2). Check `ResolveCompatibilityLevel` is available at generate time (it is — `Generate(ops, budget, context)`).
3. **SQLite JSON1 availability**: bundled SQLite always has it; still add one integration test that runs `SELECT count(*) FROM json_each('[1,2]')` so a exotic native build fails loudly with a clear message.
4. **PG array inference for exotic types** (`DateOnly`, `TimeOnly`, decimals, value-converted enums): covered by building the typed array from converted values + integration tests per type; explicit `NpgsqlDbType` only if inference fails.
5. **Threshold tuning**: 100/50/50 are starting marks from EF10 reasoning, not measurements. Benchmark `IN` vs fast path at 10/50/100/500/5000 rows on each provider before locking them.
6. **`MaxParametersPerCommand` semantics**: after this change the budget still caps everything *except* large-`IN` contents (which cost 1). Document that; do not silently ignore the budget elsewhere.

---

## 8. Test plan (must all pass before merge)

- **Unit golden-SQL (per provider):**
  - Small list (3 ids) → unchanged `IN (@p0, @p1, @p2)`.
  - 101 ids (SQL Server) → `OPENJSON(@pN) WITH ([value] int …)` + `Parameters.Count == assignments + 1 + discriminator`.
  - 51 ids (SQLite) → `json_each(@pN)`; PG → `= ANY (@pN)`.
  - Overflow backstop: `Threshold = int.MaxValue` + 5000 ids + budget 2000 → still fast path, 1 param, no throw (proves the backstop, not just the threshold).
  - Negated large list → `NOT (…)` wrapper preserved; empty list → `1=0`/`NOT (1=0)`.
  - Counting consistency: `chunk.Parameters.Count` equals the emitter's actual param count for every golden case.
- **Integration (per provider, real DB):**
  - Seed 6000 rows; `Update … Where(ids5000.Contains(x.Id)).Set(…)` → 5000 rows affected, verified by re-query; same for `Delete`; same for string/Guid id lists; same for nullable column with nulls in list.
  - Upsert guard containing a large `IN` (regression for `CountParameterNodes(spec.Guard)` path).
  - SQL Server compat<130 path (if fallback implemented) + SQLite `json_each` probe + PG array-type matrix (int/long/string/Guid/DateTime).
- **Perf smoke:** time 5000-id update before (throws / N-param) vs after (1-param) on each provider; record in PR.
- **Existing suites green:** all `ChunkingTests` / `SqliteChunkingTests` / `NpgsqlChunkingTests` throw-tests still pass (they use tiny ops, unaffected); full unit + integration suites.

---

## 9. Rollout order

1. Core helper + JSON/array builders with unit tests (no generator wiring).
2. SQL Server generator + executor + tests (highest user pain: 2100 cap).
3. PostgreSQL generator + tests.
4. SQLite generator + tests.
5. Threshold benchmark → lock marks → docs (`README`, `DESIGN`) → PR with perf numbers.
