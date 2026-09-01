# SQLite Support — Implementation Plan

> Goal: add full SQLite provider (`NSLabs.EFCore.Extensions.Sqlite`) with **zero-feature-loss**
> vs the current SQL Server provider. Every public API, semantics, option and edge-case
> that works on SQL Server must work on SQLite, modulo documented dialect deltas.

Status: planning (M4 of `docs/DESIGN.md:348`).
Provider string: `Microsoft.EntityFrameworkCore.Sqlite` (`Microsoft.Data.Sqlite`).
Targets: `net10.0`, `Microsoft.EntityFrameworkCore.Sqlite 10.0.0`, `Microsoft.EntityFrameworkCore.Relational 10.0.0`.

---

## 1. Current Architecture Recap (what we are cloning)

```
Fluent model → ModelBinder + LinqPredicateTranslator + SetExpressionTranslator → BoundOperation → Provider (Generate + Execute) → DbCommand(s) → BulkExecuteResult
```

### 1.1 Provider-neutral layer (no change, re-used as-is)

| File | What it does | SQLite impact |
|---|---|---|
| `src/NSLabs.EFCore.Extensions/BulkBatch.cs:9` | `IBulkBatch` deferred builder; binds `Update/Delete/Upsert` via `ModelBinder`/`LinqPredicateTranslator`/`SetExpressionTranslator`; validates duplicate `Set`, empty `Where`, store-generated rejection, discriminator injection, `ValidateUniqueUpsertKeys` | **Re-use verbatim.** Already validates duplicate upsert keys O(rows) per table+shape bucket — identical for SQLite `ON CONFLICT`. `MaxParametersPerCommand` default lives in `BulkExecuteOptions.cs:5` (2000) — provider must override default for SQLite (see §4.7). |
| `src/NSLabs.EFCore.Extensions/BulkOperationBuilders.cs:5` | Fluent `UpdateOperationBuilder / DeleteOperationBuilder / UpsertOperationBuilder` | Re-use. |
| `src/NSLabs.EFCore.Extensions/Internal/ModelBinder.cs:1` | `ResolveEntityType`, `GetTableName`, `GetColumnName`, `ConvertToProvider` (value converters + enum underlying), `EnsureWritable`, `AddDiscriminatorPart`, `IsBindableScalar/IsInsertBindable` | Re-use. Note: `GetTableName` → SQLite returns unqualified name (schema ignored). `GetColumnName` with `StoreObjectIdentifier` already handles SQLite (single table identifier). |
| `src/NSLabs.EFCore.Extensions/Internal/LinqPredicateTranslator.cs:1` | Predicate → `SqlNode` tree: `==/!=/IsNull`, `< ≤ > ≥`, `&& \|\| !`, `Contains/StartsWith/EndsWith/Equals`, `IsNullOrEmpty/IsNullOrWhiteSpace`, `Contains→IN`, `EF.Functions.Like` | Re-use. Emits dialect-agnostic `SqlLikeNode`, `SqlInNode`, `SqlIsEmptyNode`, etc. SQLite emitter must handle them (LIKE escaping dialect difference — see §4.5). |
| `src/NSLabs.EFCore.Extensions/Internal/SetExpressionTranslator.cs:1` | Computed `Set(x => x.Col, x => <expr>)` → `SqlNode`: arithmetic `+ - * / %`, string `+` → `Add`, `??`→`SqlCoalesceNode`, `?:`→`SqlConditionalNode`, `ToUpper/Lower/Trim/SubString/Replace/Concat`, `Length→LEN`, `Math.Abs/Ceiling/Floor/Round/Truncate` | Re-use. Emitter mapping is provider-specific (see §4.4). |
| `src/NSLabs.EFCore.Extensions/Internal/SqlNodes.cs:1` | `SqlColumnNode`, `SqlBooleanNode`, `SqlParameterNode`, `SqlBinaryNode`, `SqlNotNode`, `SqlNullCheckNode`, `SqlCoalesceNode`, `SqlConditionalNode`, `SqlMethodCallNode`, `SqlLikeNode`, `SqlInNode`, `SqlIsEmptyNode` | Re-use. |
| `src/NSLabs.EFCore.Extensions/Internal/IBulkProvider.cs:5` + `BulkProviderRegistry.cs:1` | `Generate + ExecuteAsync` abstraction + reflection fallback for SqlServer | Extend registry to resolve `Microsoft.EntityFrameworkCore.Sqlite` (see §4.1). |
| `src/NSLabs.EFCore.Extensions/BulkExecuteResult.cs:5` + `BulkExecuteOptions.cs:5` | `BulkExecuteResult { TotalRowsAffected, Operations[] }` + `BulkExecuteOptions { MaxParametersPerCommand, ThrowIfZeroAffected, CommandTimeout, OnCommandText }` | Re-use; see §4.7 for SQLite default param budget. |

### 1.2 SQL Server provider (template to copy-adapt)

| File | Responsibility | SQLite analogue to create |
|---|---|---|
| `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerProvider.cs:6` | Implements `IBulkProvider`, `ModuleInitializer` registration | `SqliteProvider.cs` |
| `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs:6` | Chunking by param budget, `EmitStatement` (UPDATE/DELETE), `EmitMerge` (MERGE … HOLDLOCK), `ParameterEmitter` (quoting `[]`, `RenderComparison`, `EmitMethod`, `Quote`), `CountParameterNodes` | `SqliteSqlGenerator.cs` |
| `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:8` | Batched single `DbCommand` per chunk, `DECLARE @rc + @@ROWCOUNT + SELECT`, `ExecuteReader` accumulation, ambient `CurrentTransaction` piggyback, `ExecutionStrategy` retry, `ThrowIfZeroAffected` post-check, `CloseConnection` handling | `SqliteExecutor.cs` (sequential `ExecuteNonQuery` path — see §4.6) |

---

## 2. SQLite Constraints & Dialect Delta (must not be missed)

| Concern | SQL Server | SQLite | Implication |
|---|---|---|---|
| **Quoting** | `[Name]` with `]]` escape `SqlServerSqlGenerator.cs:354` | `"Name"` with `""` escape; SQLite also accepts `[]`/` `` ` but canonical is `"` . Must emit `"` for new provider. Table may be `schema.table` → split on `.` and quote each segment `"` . | New `Quote(string)` + helper `QuoteTable(IEntityType)` |
| **Parameter prefix** | `@p0` | Same `@p0` supported by `Microsoft.Data.Sqlite` | Keep |
| **Param budget** | 2100 (`sp_executesql` limit) | `SQLITE_MAX_VARIABLE_NUMBER` default **999** (`sqlite3_limit` can be compiled to 32766; `Microsoft.Data.Sqlite` default 999 before 8.x, 32766 after? verify). Must chunk at 999 to be safe. Expose provider-specific default; allow caller override via `BulkExecuteOptions.MaxParametersPerCommand`. | New default constant `SqliteDefaults.MaxParametersPerCommand = 999` (or 900 safety margin). Document that `999` can be raised if user recompiled SQLite. |
| **Variables / rowcount** | `DECLARE @rc0 int; SET @rc0=@@ROWCOUNT; SELECT @rc0 AS Op0` | **No variables.** No `@@ROWCOUNT`. Closest: `changes()` (rows affected by last INSERT/UPDATE/DELETE, per connection) and `total_changes()`. Cannot do `SELECT changes()` in same batched reader reliably across driver versions? `Microsoft.Data.Sqlite` docs: batching `;`-separated statements supported but `ExecuteReader` handling with semicolons is driver-quirky. DESIGN.md:254 already anticipates: *"SQLite caveat: Microsoft.Data.Sqlite batching support is shaky → run ops sequentially in-process (no network cost anyway), wrapped in one transaction."* | Use **sequential `ExecuteNonQuery`** path: for each `PendingUnit` execute one `DbCommand`, capture `command.ExecuteNonQueryAsync()` return (== rowcount) per op, accumulate. This is in-process cost negligible. Fallback alternative (single batch + `SELECT changes()`) is fragile and not needed — but keep as opt-in comment. |
| **Upsert syntax** | `MERGE INTO … WITH (HOLDLOCK) USING (VALUES …) ON … WHEN MATCHED AND guard THEN UPDATE SET … WHEN NOT MATCHED THEN INSERT …` | `INSERT INTO "T" ("c1","c2"…) VALUES (@p0,…), (@p1,…) ON CONFLICT ("conflictCols") DO UPDATE SET "c"=excluded."c", … WHERE guard` (SQLite 3.24+/3.35). If no `DO UPDATE` payload? Must emit `DO NOTHING`? For our case, when `hasMatchedUpdatePayload==false` and guard absent, `DO NOTHING` is correct? But `BoundUpsertSpec` currently always has either explicit `Assignments` or `UpdateColumns` (full row). So there is always a payload; no DO NOTHING branch today. Keep invariant. `excluded` alias references source row. Guard `WHERE` in `DO UPDATE` references target columns (no alias `t` in SQLite; `WHERE` filters whether to update). | Emit `INSERT … ON CONFLICT (<conflict>) DO UPDATE SET … WHERE …` |
| **Conflict target requirement** | MERGE accepts any equality predicate | `ON CONFLICT (<cols>)` must match a `PRIMARY KEY` or `UNIQUE` constraint else runtime error `ON CONFLICT clause does not match any PRIMARY KEY or UNIQUE constraint`. | Let SQLite error surface with helpful wrapper: catch and re-throw with hint "ensure a Unique index exists on (<cols>)". Do not pre-validate (EF model may not have constraint). Document in plan + exception message. |
| **HOLDLOCK / isolation** | `WITH (HOLDLOCK)` required for race safety | No `HOLDLOCK` (SQLite has database-level locking; single writer). | Omit. Note in docs that SQLite serializes writers at DB level. |
| **String concat** | `+` (`[Name] + @p`) null-propagates | `||` (`"Name" || @p`) null-propagates (same). `CONCAT()` does not exist in vanilla SQLite (exists as alias in 3.44+?) → must emit `||`. | Map `SqlBinaryOperator.Add` where operands are string-typed? Currently emitter treats `+` uniformly. For SQLite need type-aware or emit `||` for string nodes. Simpler: check `SqlBinaryNode` originating from string `+` — caller uses same node. SQLite emitter should emit `||` when either side is string-typed? Or emit `||` for Add if column type is string? Conservative: emit `||` for Add when at least one operand resolves to string column or string param? Keep heuristic: if node came from `SetExpressionTranslator` string path, we still use generic `Add` node; emitter must choose: emit `||` for SQLite where the CLR type is `string`. Add check in `Emit`: `if (IsStringAdd(node)) -> "||" else "+"`. |
| **Length / string functions** | `LEN`, `LTRIM(RTRIM())` for `TRIM`, `SUBSTRING(col, start, len)`, `REPLACE`, `CONCAT`, `UPPER/LOWER` | `LENGTH`, `TRIM` / `LTRIM` / `RTRIM`, `SUBSTR(col,start,len)`, `REPLACE`, `UPPER/LOWER` same. | Map in `EmitMethod`. |
| **Numeric functions** | `ABS`, `CEILING`, `FLOOR`, `ROUND(col, n)` / `ROUND(col,n,1)` for Truncate | SQLite: `ABS` same, `CEIL`/`CEILING`? SQLite has no built-in `CEIL`/`FLOOR` without `math` extension (bundled since 3.35 with `-DSQLITE_ENABLE_MATH_FUNCTIONS`). `Microsoft.Data.Sqlite` bundles `math` on most builds (`ceil`, `floor` available). Verify at runtime; if missing, emulate with `CAST`. Simpler: emit `CEIL`/`FLOOR`. Add fallback note. `ROUND(X)` / `ROUND(X,Y)` supported (since 3.35?). Truncate `ROUND(X,0,1)` not supported — must emulate: `CAST(X AS INTEGER)` truncates toward zero? Actually `CAST(2.9 AS INTEGER)=2`, `CAST(-2.9 AS INTEGER)=-2` matches `TRUNCATE`. Document divergence. | See §4.4 table. |
| **TRIM semantics** | `LTRIM(RTRIM([Name]))` implements `TRIM` compat | `TRIM([Name])` native | Emit `TRIM`. |
| **LIKE escaping** | `EscapeLike` → `[[] [%] [_]` bracketing + no `ESCAPE` clause | SQLite `LIKE` uses `%`/`_` and optional `ESCAPE '\'`. Bracket escaping does **not** work. `EscapeLike` must change for SQLite: escape `%` → `\%`, `_` → `\_` with `ESCAPE '\'` clause, or skip escaping and rely on raw pattern. | Provide SQLite-specific `EscapeLikeSqlite` + `ESCAPE '\'` on `SqlLikeNode` emit. Also support `GLOB`? Not needed. |
| **Boolean columns** | `BIT` → `[Active]=1` via `SqlBooleanNode` | SQLite has no `BIT`; EF maps `bool` → `INTEGER` 0/1. Same `SqlBooleanNode` emit works: `"Active" = 1` . `NOT ([Active]=1)` same. | Keep. |
| **DateTime** | `GETDATE()` computed column in `TestModel` | SQLite has `CURRENT_TIMESTAMP` / `datetime('now')`; `HasComputedColumnSql("GETDATE()")` will break SQLite model creation. Test model must use provider-conditional computed SQL or avoid computed column for SQLite tests. | In `TestDbContext.OnModelCreating`, branch on `Database.IsSqlite()` or avoid setting computed for SQLite. Or remove `CreatedAt` from SQLite integration tests. |
| **Identity / ValueGenerated** | `UseIdentityColumn()` | SQLite uses `AUTOINCREMENT` or `INTEGER PRIMARY KEY AUTOINCREMENT`. EF maps `ValueGenerated.OnAdd`. Same `IsInsertBindable` guard works — store-generated columns excluded. For integration tests we disable identity via `ValueGeneratedNever` in `IntegrationTestDbContext` — same pattern for SQLite `SqliteTestBase`. | Keep. |
| **Temp tables / staging** | Strategy B (future) via `#ops` / TVP | SQLite temp tables `CREATE TEMP TABLE` supported but out of scope v1. | Out of scope. |
| **ExecutionStrategy retry** | `SqlServerRetryingExecutionStrategy` `RetriesOnFailure=true` | SQLite has `SqliteRetryingExecutionStrategy`? Not by default. `CreateExecutionStrategy().RetriesOnFailure` false normally. So no retry wrapping needed. | Executor checks same flag — naturally no-op. |
| **Schema prefix** | `dbo.Items` → `[dbo].[Items]` possible | SQLite ignores schema; `GetTableName()` returns unqualified. If schema present, strip or quote each part but SQLite will create `schema.table` as literal name — wrong. So `GetTableName` shouldreturn just table name for SQLite; EF's `GetTableName()` already does that when provider is SQLite. | No extra handling. |

---

## 3. What To Build — Project & File Checklist

### 3.1 New project

```
src/NSLabs.EFCore.Extensions.Sqlite/
  NSLabs.EFCore.Extensions.Sqlite.csproj
  Internal/
    SqliteProvider.cs            # IBulkProvider impl + ModuleInitializer
    SqliteSqlGenerator.cs        # chunking, quoting `"`, upsert INSERT…ON CONFLICT, Quote, Emit, Count
    SqliteExecutor.cs            # sequential ExecuteNonQuery + accumulation, CurrentTransaction piggyback, ThrowIfZeroAffected
```

### 3.2 Metadata / infra changes

| File | Change |
|---|---|
| `Directory.Packages.props:9` | Add `<PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />` |
| `Directory.Build.props:10` | Append package tag `sqlite` |
| `src/NSLabs.EFCore.Extensions/Internal/BulkProviderRegistry.cs:1` | Add `Microsoft.EntityFrameworkCore.Sqlite` reflection fallback `TryLoadSqliteProvider` (mirror `TryLoadSqlServerProvider`) + lookup string constant |
| `src/NSLabs.EFCore.Extensions/BulkExecuteOptions.cs:3` | Optional: keep 2000 default but document provider-specific recommended default. Alternatively add static `ForSqlite()` factory or provider overrides limit at `Generate` time if caller left default (requires passing provider default). Simplest: generator clamps `maxParametersPerCommand == 2000` default → treat as 999 for SQLite? Better: in `BulkBatch.ExecuteAsync` pass `options.MaxParametersPerCommand == 2000` untouched; inside `SqliteSqlGenerator.Generate` enforce `effectiveLimit = Math.Min(maxParametersPerCommand, 999)` or just rely on caller to know? **Decision:** generator uses whatever budget passed; default from `BulkExecuteOptions` stays 2000 for backward compat but `SqliteProvider.Generate` will cap at `min(requested, 999)` and log? Better to change `BulkExecuteOptions` default handling: if caller didn't set (==2000), SQLite uses 999. Document. |
| `src/NSLabs.EFCore.Extensions.slnx:10` | Add `<Project Path="src/NSLabs.EFCore.Extensions.Sqlite/NSLabs.EFCore.Extensions.Sqlite.csproj" />` |

### 3.3 csproj content (template)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>NSLabs.EFCore.Extensions</RootNamespace>
    <PackageId>NSLabs.EFCore.Extensions.Sqlite</PackageId>
    <Description>SQLite provider for NSLabs.EFCore.Extensions</Description>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\NSLabs.EFCore.Extensions\NSLabs.EFCore.Extensions.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="NSLabs.EFCore.Extensions.Tests.Unit" />
    <InternalsVisibleTo Include="NSLabs.EFCore.Extensions.Tests.Integration" />
  </ItemGroup>
</Project>
```

### 3.4 Tests

```
tests/NSLabs.EFCore.Extensions.Tests.Unit/
  SqliteHarness.cs               # helper mirroring Harness.cs but calls SqliteSqlGenerator.Generate
  SqliteGoldenSqlTests.cs        # update diagrams quoted with "
  SqliteUpsertGoldenSqlTests.cs  # ON CONFLICT
  SqliteComputedSetGoldenSqlTests.cs
  SqliteChunkingTests.cs
  SqlitePredicateTests.cs        # LIKE escaping, IN, etc.

tests/NSLabs.EFCore.Extensions.Tests.Integration/
  SqliteFixture.cs               # in-memory or temp-file per run (see §6)
  SqliteTestBase.cs
  SqliteUpdateExecutionTests.cs
  SqliteUpsertExecutionTests.cs
  SqliteComputedSetExecutionTests.cs
  SqliteTransactionAndSemanticsTests.cs
  SqlitePredicateTranslationExecutionTests.cs
```

### 3.5 Samples

```
samples/NSLabs.EFCore.Extensions.Samples.Shared/   # no change (provider-agnostic scenarios already)
samples/NSLabs.EFCore.Extensions.Samples.Sqlite/   # thin host, mirrors SqlServer host: UseSqlite("Data Source=…")
```

### 3.6 Docs

| Doc | Update |
|---|---|
| `README.md:20` | Add `dotnet add package NSLabs.EFCore.Extensions.Sqlite`, prerequisites |
| `docs/DESIGN.md:230` | Expand §4 Strategy A SQLite section (replace caveat with actual impl + rowcount via changes()) |
| `docs/TRANSACTIONS.md` | Add SQLite paragraph (autocommit per statement; database-level lock serialization) |
| `docs/COMPUTED_SET_SUPPORT.md:65` | Add SQLite column in provider notes + function mapping table |
| `docs/TESTING.md` | Add SQLite unit + integration instructions (no Docker needed; in-memory) |
| `NUGET_README.md` | Mention SQLite package |

---

## 4. Detailed Feature Spec — Nothing Missed

### 4.1 Provider registration & resolution

```csharp
// src/NSLabs.EFCore.Extensions.Sqlite/Internal/SqliteProvider.cs
internal sealed class SqliteProvider : IBulkProvider
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.Sqlite";
    public IReadOnlyList<SqlChunkPlan> Generate(...) => SqliteSqlGenerator.Generate(...);
    public Task<Dictionary<int,int>> ExecuteAsync(...) => SqliteExecutor.ExecuteAsync(...);
}
internal static class SqliteProviderRegistration
{
    [ModuleInitializer] internal static void Register() => BulkProviderRegistry.Register(new SqliteProvider());
}
```

`BulkProviderRegistry.cs` add:

```csharp
if (providerName == "Microsoft.EntityFrameworkCore.Sqlite")
{
    var loaded = TryLoadSqliteProvider();
    …
}
private static IBulkProvider? TryLoadSqliteProvider() => Type.GetType(
    "NSLabs.EFCore.Extensions.Internal.SqliteProvider, NSLabs.EFCore.Extensions.Sqlite", false)
    is Type t ? Activator.CreateInstance(t) as IBulkProvider : null;
```

Failure mode if package not referenced → existing `NotSupportedException` with hint
`"Ensure the matching NSLabs.EFCore.Extensions.* provider package is referenced (e.g. NSLabs.EFCore.Extensions.Sqlite for SQLite)."`

### 4.2 Quoting

```csharp
internal static string Quote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";
internal static string QuoteTable(IEntityType et) {
    var raw = ModelBinder.GetTableName(et); // e.g. "Items" or "dbo.Items" (if ever)
    return string.Join(".", raw.Split('.').Select(Quote));
}
```

All column/table emits switch from `SqlServerSqlGenerator.Quote` (`[]`) to `SqliteSqlGenerator.Quote` (`""`).

### 4.3 Chunking (copy-adapt `SqlServerSqlGenerator.Generate`)

Reuse identical pending-buffer logic `PendingUnit`, `ExpandUpsert`, `FlushPending`, `CountParameters`,
but with SQLite defaults. Key invariants preserved:

- Submission order == execution order (`OperationIndices` distinct per chunk).
- Upsert row splitting across chunks with `fixedCost + perRowCost`.
- `cost > max` throws `InvalidOperationException` with guidance.
- Zero-row upsert → single unit `RowCount==0` → `BuildChunk` emits no INSERT but still contributes to rowcount mechanism (see §4.6: just return 0 for that index).

Effective limit resolution (pick one, document):

```csharp
// Option A: provider clamps
var effectiveLimit = Math.Min(maxParametersPerCommand, SqliteDefaults.MaxParametersPerCommand);
```

`SqliteDefaults`:

```csharp
internal static class SqliteDefaults { public const int MaxParametersPerCommand = 999; }
```

Document: callers who recompile SQLite with higher `SQLITE_MAX_VARIABLE_NUMBER` can pass larger `BulkExecuteOptions.MaxParametersPerCommand`.

### 4.4 SQL generation — per statement

#### 4.4.1 UPDATE

```sql
UPDATE "Items" SET "Key1" = @p0, "Key2" = @p1 WHERE "Id" = @p2;
```

Same as `EmitStatement` `BulkOperationKind.Update` branch but with SQLite quoting and `ParameterEmitter`.
Computed assignments `ValueExpression is not null` → `emitter.Emit(expr, entityType)` (no alias).

#### 4.4.2 DELETE

```sql
DELETE FROM "Items" WHERE "Created" < @p0;
```

#### 4.4.3 Upsert — `EmitInsertOnConflict`

```sql
INSERT INTO "Customers" ("Code", "Active", "Name")
VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)
ON CONFLICT ("Code") DO UPDATE SET "Active" = excluded."Active", "Name" = excluded."Name"
-- with guard:
ON CONFLICT ("Code") DO UPDATE SET "Active" = excluded."Active" WHERE "Active" = 1
-- with explicit Set(...) computed:
ON CONFLICT ("Id") DO UPDATE SET "Key3" = @p7 WHERE NOT ("Active" = 1)
-- when guard references target columns, emit with quoted column refs (no alias):
ON CONFLICT ("Code") DO UPDATE SET "Name" = excluded."Name" WHERE "Active" = 1
```

Rules derived from `SqlServerSqlGenerator.EmitMerge:212`:

- `insertColumnNames` = `spec.InsertColumns.Select(c => GetColumnName(...))`.
- `conflictProperties` → `ON CONFLICT (<quoted conflict cols>)`.
- Determine `hasMatchedUpdatePayload = Assignments.Count>0 || UpdateColumns.Count>0`. If false → `DO NOTHING`? In current model this branch not reached (always has payload), but guard against it for forward compat: emit `DO NOTHING` and return.
- If `Assignments.Count>0` → iterate assignments: `Quote(col) + " = " + (IsComputed ? Emit(computed, entityType) : "excluded."+Quote(col) ???` Wait: for SQLite, constant Set vs computed Set: constant `Set(x=>x.Key3, 9)` should emit `"Key3" = @p7`. For non-computed row-wide update `UpdateColumns`, emit `"Col" = excluded."Col"`. For computed explicit Set, emit `"Col" = (<computed expr>)` where computed references target? In SQL Server computed inside MERGE uses `[t].[Col]` alias. In SQLite there is no `t` alias in DO UPDATE SET — the target row's current values are accessed via bare column name? Actually `DO UPDATE SET` allows referencing target table columns implicitly. Best to emit column refs as `"<col>"` (i.e., target) for computed expressions where translator would have emitted `[t].[Col]`. For SQLite, call `emitter.Emit(computed, entityType)` without alias — it emits `"Col"` which SQLite interprets as target's current value (not `excluded`). Correct.
- For row-wide path (`Assignments.Count==0`), loop `UpdateColumns` → `"Col" = excluded."Col"`.
- Guard: `spec.Guard is not null` → append `WHERE ` + `emitter.Emit(guard, entityType)` . Guard translator already emits bare column refs like `"Active" = 1` which is exactly SQLite's WHERE target filter.
- Composer must handle `VALUES` row tuples parameterization same as SQL Server.

Edge: SQLite does **not** allow `excluded` to be used in guard `WHERE` directly? `WHERE` can reference both `excluded.col` and target col — we only need target column guard today (same as SQL Server guard `t.Status != …`). So emit target column form.

Zero-row guard: `spec.Rows.Count==0` → no INSERT, cost 0, treat specially (still need Op index). See chunk builder.

#### 4.4.4 Predicate emitting

`EmitPredicate` identical: `string.Join(" AND ", parts.Select(p => emitter.Emit(p, entityType)))` plus `DiscriminatorPart` already included.

#### 4.4.5 Parameter counting

Reuse `CountParameters` + `CountParameterNodes` switch including `SqlLikeNode=>1`, `SqlInNode=>Values.Count`, `SqlIsEmptyNode=>0`, plus computed method args summation.

#### 4.4.6 Emitter differences (SQLite vs SQL Server)

| Node | SQL Server emit (`SqlServerSqlGenerator.ParameterEmitter`) | SQLite emit |
|---|---|---|
| `SqlColumnNode` | `[alias.] + Quote(col)` → `[t].[Col]` or `[Col]` | `Quote(col)` (no alias) — or `WithAlias` returns `""` and `Quote` is `"` . For computed targeting, no `t` alias; SQLite uses bare col. |
| `SqlBooleanNode` | `[Active] = 1` | `"Active" = 1` same pattern with `"` |
| `SqlNullCheckNode` | `[Col] IS [NOT] NULL` | same |
| `SqlLikeNode` | `[Col] LIKE @p` / `NOT LIKE` | `"Col" LIKE @p ESCAPE '\' ` / `NOT LIKE` — patternValue pre-escaped for SQLite (see below) |
| `SqlInNode` | `[Col] IN (@p0,…)` | same quoting |
| `SqlIsEmptyNode` | `([Col] IS NULL OR [Col] = '')` | same |
| `SqlBinaryNode` arithmetic | `([A] + @p)` etc using `+` | same but string `Add` must emit `||` |
| `SqlUnaryNode Negate` | `-[Col]` | same |
| `SqlConditionalNode` | `CASE WHEN … THEN … ELSE … END` | same |
| `SqlCoalesceNode` | `COALESCE(…, …)` | same |
| `SqlMethodCallNode` | see `EmitMethod` switch | see table below |

#### 4.4.7 Computed method mapping

Copy `SqlServerSqlGenerator.ParameterEmitter.EmitMethod` switch and adapt:

| Method | SQL Server emit | SQLite emit | Note |
|---|---|---|---|
| `UPPER` | `UPPER(col)` | `UPPER(col)` | same |
| `LOWER` | `LOWER(col)` | `LOWER(col)` | same |
| `TRIM` | `LTRIM(RTRIM(col))` | `TRIM(col)` | native |
| `LTRIM` | `LTRIM(col)` | `LTRIM(col)` | same |
| `RTRIM` | `RTRIM(col)` | `RTRIM(col)` | same |
| `LEN` | `LEN(col)` | `LENGTH(col)` | rename |
| `SUBSTRING` (2/3 args) | `SUBSTRING(col, start+1, len)` | `SUBSTR(col, start+1, len)` | rename; one-arg overload → `SUBSTR(col, start+1)` or `SUBSTR(col,start+1, LENGTH(col))` — keep same LEN translation but use LENGTH |
| `REPLACE` | `REPLACE(col,@p,@p)` | same | |
| `CONCAT` | `CONCAT(a,b,…)` | `(a || b || …)` or `a || b` chaining — SQLite `CONCAT` not universal. Emit ` (arg0 || arg1 || …)` . If single arg: pass through. | Ensure null propagation same as `+`; `||` already matches `+` semantics (NULL propagates). |
| `ABS` | `ABS(col)` | `ABS(col)` | |
| `CEILING` | `CEILING(col)` | `CEIL(col)` or `CEILING(col)` — check availability. Provide `CAST((CASE WHEN col = CAST(col AS INTEGER) THEN col ELSE CAST(col AS INTEGER)+1 END)…)` fallback note. Simplest: emit `CEIL(col)` (SQLite 3.44+ math, alias `CEILING` may work). | Document requirement `SQLITE_ENABLE_MATH_FUNCTIONS`. |
| `FLOOR` | `FLOOR(col)` | `FLOOR(col)` | |
| `ROUND` (1 arg) | `ROUND(col,0)` | `ROUND(col)` or `ROUND(col,0)` | SQLite `ROUND(X)` / `ROUND(X,Y)` both work. Emit `ROUND(col,0)`. |
| `ROUND` (2 args) | `ROUND(col,n)` | `ROUND(col,n)` | same |
| `ROUND` (3 args truncate) | `ROUND(col,0,1)` | `CAST(col AS INTEGER)` or `TRUNC` emulation → Emit `CAST(col AS INTEGER)` when source `Truncate` node. | Need to capture `Truncate` node's lowering already as `ROUND(_,0,1)` in translator — emitter must detect 3-arg ROUND and replace with `CAST`. |

String `+` handling: in `Emit`, detect if binary is string concat. Options:
```csharp
private static bool IsStringConcat(SqlBinaryNode n, IEntityType et) =>
    n.Operator == SqlBinaryOperator.Add && (
       et.FindProperty(...)?.ClrType == typeof(string)  // heuristic
    );
```
Simpler: at `TranslateBinary` time, distinguish string Add vs numeric Add via operand CLR types. Today translator conflates them. SQLite emitter can do type-aware check: if either operand's inferred type is string, emit `||`. Infer via `SqlColumnNode.Property.ClrType == typeof(string)` or `SqlParameterNode.Value is string`. Implement helper.

### 4.5 Predicate translation LIKE escaping (must-fix)

Current `LinqPredicateTranslator.EscapeLike:279` escapes `[ % _` → `[[] [%] [_]` for SQL Server bracketing.
For SQLite, bracketing not understood. SQLite `LIKE` escaping is `ESCAPE '\'` with `\% \_`.

Add provider-aware escaping: keep current `EscapeLike` for SQL Server nodes (patternValue already escaped). For SQLite emitter, patternValue arrives already escaped in SQL Server style — would break SQLite.
Solution: **do not reuse escaped value**; make `LinqPredicateTranslator` escaping dialect-agnostic or make it emit `SqlLikeNode` with raw pattern + kind, and let emitter escape.

Two options:

- Option A (minimal): `LinqPredicateTranslator` keeps producing `SqlLikeNode` with server-escaped pattern; `SqliteSqlGenerator.ParameterEmitter.EmitLike` detects bracket escapes and converts `[%]`→`\%`. Quick fix.
- Option B (clean): Modify `SqlLikeNode` to store `Kind` + raw pattern; let each provider emitter do own escaping `EscapeLikeSqlite: pattern.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")` and emit `ESCAPE '\'`. Requires translators to pass raw pattern not escaped? Current translator already escapes. Change translator to pass raw and emitter escapes per-dialect — but that would be a breaking change for SQL Server golden tests (patterns already escaped). Could handle by having emitter not double-escape if already escaped.

**Recommended:** Introduce `SqlLikeNode.PatternValue` as raw (change `TranslateMethodCall` to produce raw `%pattern%` without `EscapeLike`). Then `SqlServerSqlGenerator.EmitLike` calls `EscapeLike` Server-style, `SqliteSqlGenerator.EmitLike` calls SQLite-style. This touches `LinqPredicateTranslator.cs:163` etc. Provide migration shim: if pattern already contains `[%]` detection, skip.

Add `ESCAPE '\'` clause on SQLite emit: ` $"{col} {(negated ? "NOT LIKE" : "LIKE")} {param} ESCAPE '\\'"`.

Same for `EF.Functions.Like` — pattern is user-supplied with `%/_` wildcards already; no escaping, just pass through but add `ESCAPE`.

Document escaping difference in `docs/COMPUTED_SET_SUPPORT.md`.

### 4.6 Rowcount & execution strategy

#### Chosen approach: **Sequential per-operation `ExecuteNonQuery`**

Rationale: SQLite is in-process (file or memory). No wire RTT. Executing N operations as N `ExecuteNonQuery` calls within a single `DbConnection` (and optional single transaction) costs O(N) syscall but negligible (< few ms per op) and is robust across `Microsoft.Data.Sqlite` versions. Batching into single `DbCommand` with `;` + `SELECT changes()` requires `ExecuteReader` multi-result-set support which is driver-quirky and not faster on file.

```
ExecuteAsync flow (SqliteExecutor):

1. database = context.Database
2. connection = database.GetDbConnection()
3. transaction = database.CurrentTransaction?.GetDbTransaction()  // piggyback
4. shouldClose = check closeConnection flag same as SqlServerExecutor:15
   if CurrentTransaction != null => closeConnection=false else check executionStrategy.RetriesOnFailure
5. RunAsync:
   counts = Dictionary<int,int> zeroed per GlobalIndex
   shouldCloseConnection handling Open/Close via database.OpenConnectionAsync
   call ExecuteCoreAsync -> per chunk:
     // Option: chunk is still logical grouping for parameter budget;
     // physically execute each PendingUnit inside chunk sequentially as separate command
     foreach unit in chunk.Units:
       if upsert zero-rows: counts[GlobalIndex] = 0; continue
       build sqlText for single unit (reuse BuildChunk per-unit path or inline emit)
       create command, assign transaction, CommandTimeout
       add parameters
       OnCommandText?.Invoke(command.CommandText)   // still fire
       rows = await command.ExecuteNonQueryAsync(ct)
       counts[GlobalIndex] = counts.GetValueOrDefault(idx) + rows  // handles upsert row-split accumulation
   6. post-loop ThrowIfZeroAffected same as SqlServerExecutor:65
```

Key details:

- **Per-chunk still matters** for `MaxParametersPerCommand`; we cannot build one huge `INSERT VALUES (…),(…)` beyond 999. Chunking already splits upsert rows per §4.3. Sequential execution respects chunks — each chunk's units each become one command, but chunk grouping is not needed for correctness besides budget; still execute in order.
- **Parameter naming**: each command's parameters restart at `@p0` (per-chunk isolation already). So no cross-command name clash.
- **Quoting & param values**: reuse `SqlParam` list per unit.
- **Command reuse**: create/dispose per unit (using).
- **Upsert row-split accumulation**: same `counts[index] += rows` where one logical `BoundOperation` (upsert) spans multiple chunks (multiple INSERTs). `rows` per INSERT is total rows inserted+updated by that INSERT batch. Need to distinguish SQLite rowcount for upsert: `ExecuteNonQuery` returns total rows affected by last statement (sum of INSERTs+UPDATEs inside that batch). For multi-row INSERT `ON CONFLICT` with 2 rows, if one inserts and one updates, return 2. Correct to accumulate.
- **UPDATE/DELETE zero-match**: `ExecuteNonQuery` returns 0 → `ThrowIfZeroAffected` fires after loop (inside ambient transaction caller can roll back; without transaction prior successful ops already committed/autocommitted — same caveat as SQL Server docs).
- **Alternative batch + reader** left as code comment with `SELECT changes()` sketch for future if batching needed for thousands-of-ops optimization. Not v1.

Transaction handling specifics:

```csharp
if (database.CurrentTransaction != null)
    return RunAsync(closeConnection:false)
strategy = database.CreateExecutionStrategy()
if (strategy.RetriesOnFailure) return strategy.ExecuteAsync(...)
return RunAsync(closeConnection:true)
```

Inside `RunAsync`:
```csharp
if (closeConnection && connection.State == Closed) {
   await database.OpenConnectionAsync(ct);
   shouldCloseConnection = true; // close at end
}
// No explicit transaction creation! Follow DESIGN: piggyback only.
// If SQLite DESIGN says "wrapped in one transaction", we respect opt-in:
//   caller does BeginTransactionAsync to get atomic. Our sequential autocommit mimics SQL Server per-statement commit.
//   Do NOT auto-wrap in BeginTransaction. Document that same as SQL Server.
try { await ExecuteCoreAsync(..., counts, ...); if (ThrowIfZeroAffected) throw... }
finally { if (shouldCloseConnection) await database.CloseConnectionAsync(); }
```

For SQLite in-memory tests (`DataSource=:memory:`), connection must stay open for lifetime of DB; `Testcontainers` not used. Use `DataSource=:memory:` with `Cache=Shared` or keep fixture holding open connection. Document `SqliteFixture` handling:

```csharp
var connection = new SqliteConnection("DataSource=:memory:");
await connection.OpenAsync();
optionsBuilder.UseSqlite(connection);
...
```

Or `DataSource=file:memdb1?mode=memory&cache=shared` to share across contexts. Pick explicit shared-cache file name per test run.

### 4.7 `BulkExecuteOptions` defaults

| Option | SQL Server default | SQLite recommended |
|---|---|---|
| `MaxParametersPerCommand` | 2000 | **999** (or 900 safety) |
| `ThrowIfZeroAffected` | false | same |
| `CommandTimeout` | null (provider default) | same |
| `OnCommandText` | null | same |

Implementation choice: keep class default at 2000 (avoid breaking SQL Server callers). In `SqliteSqlGenerator.Generate` do:

```csharp
var effectiveLimit = maxParametersPerCommand;
if (maxParametersPerCommand == 2000) effectiveLimit = SqliteDefaults.MaxParametersPerCommand; // honor override only if user explicitly passed
// Better: always clamp to 999 regardless:
effectiveLimit = Math.Min(maxParametersPerCommand, SqliteDefaults.MaxParametersPerCommand);
```

Option **clamp** is safest (caller passing 2000 explicitly would still be wrong for SQLite — would throw later with unclear error). So clamp.

Also document `OnCommandText` fires per physical `DbCommand` (sequential mode = N calls, not 1 batch). Make behavior explicit.

### 4.8 Computed SET whitelist — SQLite coverage parity

Must support same `SetExpressionTranslator` whitelist (`docs/COMPUTED_SET_SUPPORT.md:6`):
arithmetic, string `+`, `??`, `?:`, `== != < ≤ > ≥`, `&& || !`, casts, `ToUpper/ToLower/Trim*/Substring/Replace/Concat/Length`, `Math.Abs/Ceiling/Floor/Round/Truncate`, `Conditional` tests reuse `LinqPredicateTranslator` shapes.

Executor mapping above ensures full parity except dialect rename.

Add SQLite integration golden for each method (see §6).

Not supported list stays same; `NotSupportedException` message same.

### 4.9 Other feature parity checklists

| Feature | Where validated | SQLite handling |
|---|---|---|
| `Update<TEntity>(IEnumerable<TEntity> rows)` + custom match `Update<TEntity>(rows, match)` | `BulkBatch.BindEntityRows:331` creates one `BoundOperation` per row with PK predicate or custom `match` rewrite | Re-use — predicates become `WHERE pk=@p` + discriminator |
| `Delete` | `BindDelete` | Re-use |
| `Upsert` with `On`, `WhenMatched`, `Set`, `Values` | `BindUpsert:206` validates conflict cols not in Set, discriminator injection, updateColumns fallback, guard translation | Re-use |
| `Value converters` (enum→int etc.) | `ModelBinder.ConvertToProvider` on constants + assignments + predicate params | Re-use — SQLite type mapping still uses same converter |
| `NULL` handling | `IS NULL` generation + `IN` empty `1=0` | Same |
| `Guid/composite/string keys` | `BoundUpsertRow.KeyValues` + grouping shape | Same |
| `Discriminator(TPH)` | `AddDiscriminatorPart` | Same — column `"PetType" = 'Cat'` with `"` |
| `TPT` blocked | (not explicitly blocked today? check) | Keep — throw `NotSupportedException` if `TPT` detected via `entityType.GetTableName` shard? Document |
| `Store-generated rejection` | `EnsureWritable` | Same |
| `Per-op RowsAffected` capability flag | `DESIGN.md:172` `SupportsPerOperationCounts` | SQLite **supports** via sequential `ExecuteNonQuery` per op → set `SupportsPerOperationCounts = true` in provider metadata if exposed; otherwise still return per-op dict. Document as supported. |
| `ThrowIfZeroAffected` + transaction interaction | `SqlServerExecutor.cs:64` | Clone same post-loop check; document same caveat (no rollback without ambient tx) |
| `ExecuteUpdate` sequential semantics | Statements execute in submission order, later see earlier writes | Preserved by sequential `ExecuteNonQuery` in order |
| `Empty batch` no-op | `BulkBatch.ExecuteAsync:62` early return | Same |
| `Reusability` | `ExecuteAsync` does not clear `_operations` | Same |

---

## 5. Testing Plan — Must Cover All Features

### 5.1 Unit (no Docker) — golden-SQL

Add `SqliteHarness` mirroring `Harness.cs:9`:

```csharp
internal static class SqliteHarness {
  public static (string Sql, IReadOnlyList<SqlParam> Params) GenerateSingle(Action<IBulkBatch> build, BulkExecuteOptions? opts=null) {
    var chunks = Generate(build, opts);
    Assert.True(chunks.Count==1);
    return (Normalize(chunks[0].CommandText), chunks[0].Parameters);
  }
  public static IReadOnlyList<SqlChunkPlan> Generate(Action<IBulkBatch> build, BulkExecuteOptions? opts=null) {
    using var ctx = new TestDbContext(); // but UseSqlite connection to get model? Model is provider-agnostic until UseProvider
    // Need SQLite model: create DbContextOptionsBuilder<TestDbContext>().UseSqlite("DataSource=:memory:")
    // Or rely on SqliteTestDbContext pre-configured model (see TestModel branching)
    var batch = new BulkBatch(ctx);
    build(batch);
    return SqliteSqlGenerator.Generate(batch.Operations, opts?.MaxParametersPerCommand ?? SqliteDefaults.MaxParametersPerCommand);
  }
}
```

Tests mirror `UpdateGoldenSqlTests.cs:5`, `UpsertGoldenSqlTests.cs:5`, `ComputedSetGoldenSqlTests.cs:5`:

- `SqliteUpdateGoldenSqlTests` — verify `UPDATE "Items" SET "Key1" = @p0 WHERE "Id" = @p1`, quoting `"`, `AND "PetType" = @p2"` discriminator, `IS NULL`, `=1` booleans, etc.
- `SqliteUpsertGoldenSqlTests` — verify `INSERT INTO "Customers" … VALUES … ON CONFLICT ("Code") DO UPDATE SET …` , guard `WHERE "Active" = 1`, discriminator `PetType` present, zero-row `INSERT`-free path (emits no command or special handling).
- `SqlitePredicateLikeTests` — `Contains` → `"Col" LIKE @p ESCAPE '\'` pattern `%…%` escaped as `\%`, `IN` expansion, `IsNullOrEmpty`.
- `SqliteComputedSetGoldenSqlTests` — arithmetic `( "Amount" * @p)`, string `||` vs `+`, `TRIM/LENGTH/SUBSTR/REPLACE`, `CONCAT` as `||`, `UPPER/LOWER`, `ABS/CEIL/FLOOR/ROUND`, `COALESCE`, `CASE WHEN`.
- `SqliteChunkingTests` — same as `ChunkingTests.cs:5` but with limit 999 vs 2000.

All assert `CommandText` normalized whitespace.

### 5.2 Integration (needs Microsoft.Data.Sqlite only, no Testcontainers)

Fixture:

```csharp
public sealed class SqliteFixture : IAsyncLifetime {
  public string ConnectionString => "DataSource=file:nsbulk_sqlite_tests?mode=memory&cache=shared";
  private SqliteConnection? _keepAlive;
  public async Task InitializeAsync(){
     _keepAlive = new SqliteConnection(ConnectionString);
     await _keepAlive.OpenAsync();
     await using var ctx = CreateContext();
     await ctx.Database.EnsureCreatedAsync();
  }
  public TestDbContext CreateContext(){
     var opts = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(ConnectionString).Options;
     return new SqliteTestDbContext(opts); // ValueGeneratedNever tweaks similar to IntegrationTestDbContext
  }
  public async Task DisposeAsync(){ _keepAlive?.Dispose(); }
}
public sealed class SqliteTestDbContext(DbContextOptions<TestDbContext> o): TestDbContext(o){
  protected override void OnModelCreating(ModelBuilder b){
    base.OnModelCreating(b);
    // Override computed column SQL for SQLite:
    b.Entity<Item>().Property(x=>x.CreatedAt).HasComputedColumnSql("CURRENT_TIMESTAMP");
    b.Entity<Item>().Property(x=>x.Id).ValueGeneratedNever(); // deterministic seeds
    // same for AuditLog.Pet as SqlServer fixture
  }
}
```

Tests mirror `SqlServerFixture.cs:8` patterns but use `[Fact]`/`[SkippableFact]` without container guard (SQLite always available). Candidate files:

- `SqliteUpdateExecutionTests.cs` — seed Items, run BulkUpdate where Id=6, assert DB value.
- `SqliteUpsertExecutionTests.cs` — insert conflict+non-conflict, verify insert vs update + guard leaves untouched.
- `SqliteComputedSetExecutionTests.cs` — each computed variant persists: `Key1 + "_suf"`, `Key2*2`, `COALESCE`, `CASE`, `UPPER` etc.
- `SqliteTransactionAndSemanticsTests.cs` — ambient transaction piggyback, sequential semantics (op2 sees op1 write), ThrowIfZeroAffected rollback vs autocommit, chunk split across transactions.
- `SqlitePredicateTranslationExecutionTests.cs` — `LIKE`, `IN`, `IsNullOrEmpty`.

Run via `dotnet test tests/… -c Release` (no Docker needed).

---

## 6. Additional Files to Add / Touch (complete checklist)

- [ ] `src/NSLabs.EFCore.Extensions.Sqlite/NSLabs.EFCore.Extensions.Sqlite.csproj`
- [ ] `src/NSLabs.EFCore.Extensions.Sqlite/Internal/SqliteProvider.cs`
- [ ] `src/NSLabs.EFCore.Extensions.Sqlite/Internal/SqliteSqlGenerator.cs`
- [ ] `src/NSLabs.EFCore.Extensions.Sqlite/Internal/SqliteExecutor.cs`
- [ ] `src/NSLabs.EFCore.Extensions/Internal/BulkProviderRegistry.cs` — add SQLite resolver
- [ ] `Directory.Packages.props` — add `Microsoft.EntityFrameworkCore.Sqlite 10.0.0`
- [ ] `NSLabs.EFCore.Extensions.slnx` — add Sqlite project
- [ ] `tests/NSLabs.EFCore.Extensions.Tests.Unit/*.cs` — new Sqlite golden suite + `SqliteHarness.cs`
- [ ] `tests/NSLabs.EFCore.Extensions.Tests.Integration/SqliteFixture.cs`, `SqliteTestBase.cs`, `Sqlite*ExecutionTests.cs`
- [ ] `samples/NSLabs.EFCore.Extensions.Samples.Sqlite/Program.cs`, `.csproj`, `docker-compose` (optional — SQLite file, no container)
- [ ] `samples/NSLabs.EFCore.Extensions.Samples.Shared/Scenarios` — optional SQLite host factory
- [ ] `docs/DESIGN.md` — update §4 Strategy A SQLite paragraph
- [ ] `docs/COMPUTED_SET_SUPPORT.md` — add SQLite column
- [ ] `docs/TRANSACTIONS.md` — add SQLite note
- [ ] `docs/TESTING.md` — add SQLite section
- [ ] `docs/SQLITE_SUPPORT_PLAN.md` — this doc
- [ ] `.github/workflows/*.yml` — add `dotnet test` matrix already covers integration (no Docker needed for SQLite)
- [ ] `NUGET_README.md` / `README.md` — add Sqlite install line

---

## 7. Implementation Order (suggested milestones inside M4)

### Phase M4a — Skeleton + quoting + update/delete

1. Create `Sqlite` csproj + `BulkProviderRegistry` entry. Verify `Resolve("Microsoft.EntityFrameworkCore.Sqlite")` returns provider.
2. Copy-adapt `SqliteSqlGenerator` for UPDATE/DELETE only (reuse chunking, quoting `"`, `EmitStatement`, `EmitPredicate`, `ParameterEmitter`). Make `CountParameterNodes` pass.
3. Add `SqliteHarness` + `SqliteUpdateGoldenSqlTests` — get 5 greens.
4. Implement `SqliteExecutor` sequential `ExecuteNonQuery` path for UPDATE/DELETE only. Add `SqliteUpdateExecutionTests` against `DataSource=:memory:`.

### Phase M4b — Upsert

5. Implement `EmitInsertOnConflict` in generator (conflict target, `excluded.` SET, guard WHERE). Add `SqliteUpsertGoldenSqlTests`.
6. Extend executor for UPSERT accumulation (same `ExecuteNonQuery` path, rows includes conflict updates). Add `SqliteUpsertExecutionTests`.

### Phase M4c — Computed SET + predicates

7. Adapt `EmitMethod` for SQLite dialects; fix string `+` → `||`, `LEN`→`LENGTH`, `SUBSTRING`→`SUBSTR`, `CONCAT`→`||`, `TRIM`, `CEIL/FLOOR`, `ROUND` truncate, `LIKE ESCAPE`.
8. Add `SqliteComputedSetGoldenSqlTests` + execution tests.

### Phase M4d — Chunking & edge matrix

9. Add `SqliteChunkingTests` (param 999), overlapping-filter ordering, duplicate key detection (already covered), zero-match, enums/converters, TPH discriminator, entity-style rows.
10. Add `SqliteTransactionAndSemanticsTests` (ambient tx, ThrowIfZeroAffected, sequential).

### Phase M4e — Polish

11. Samples host `Samples.Sqlite`, docs, CI, `InternalsVisibleTo` verif, package pack.

Each phase ends with `dotnet test tests/NSLabs.EFCore.Extensions.Tests.Unit -c Release` green and dedicated SQLite integration suite green.

---

## 8. Open Questions & Risk Mitigations

| Question | Recommendation | Mitigation |
|---|---|---|
| **LIKE escaping correctness** | Make `SqlLikeNode` carry raw pattern; emitter escapes per dialect. Add test covering `% _ [` in SQLite Contains. | Implement `EscapeLikeSqlite` correctly; add integration test inserting `Key1="a%b_c"` then `Where(x=>x.Key1.Contains("a%b"))` must match escaped literal not wildcard. |
| **CEIL/FLOOR availability** | Probe `Microsoft.Data.Sqlite` bundle: `SELECT CEIL(1.2)` — if throws `no such function`, fall back to `CAST` expression. | Add runtime detection in generator: try/fallback or emit `CAST` fallback that works everywhere: `CASE WHEN x = CAST(x AS INTEGER) THEN x WHEN x>0 THEN CAST(x AS INTEGER)+1 ELSE CAST(x AS INTEGER)-1 END` . Simpler to emit `CEIL` and document prerequisite `math` extension. |
| **TRUNCATE** | SQLite `ROUND(X,0,1)` not available — must emit `CAST(X AS INTEGER)` | Detect 3-arg ROUND node and translate to CAST. |
| **In-memory DB per-test isolation** | `DataSource=:memory:` per connection is isolated → fixture shared-cache file name must be used. For per-test isolation, `EnsureDeleted + EnsureCreated` or delete rows between tests. | Use `Database.EnsureCreated()` once in fixture; per test do `ExecuteDelete` / `DELETE FROM` cleanup. |
| **GUID / decimal storage** | SQLite stores everything typelessly; EF maps `Guid`→TEXT, `decimal`→TEXT/REAL. Bulk params will pass .NET types; ensure `ConvertToProvider` still called. | Integration test matrix includes `Guid`, composite keys (add entity), `string` keys. |
| **ON CONFLICT requires unique index** | Our tests create model with `[Key]`? For `Customer.Code` we `HasAlternateKey`? Currently `Customer` PK is `Id`, `Code` not unique — SQLite `ON CONFLICT (Code)` will fail. Need to ensure test model declares unique constraint: `modelBuilder.Entity<Customer>().HasIndex(x=>x.Code).IsUnique();` | Update `TestDbContext.OnModelCreating` for SQLite fixture to create unique index, or choose PK column as conflict target in tests. Golden SQL tests ignore constraint, integration tests need real constraint. Add conditional unique in `SqliteTestDbContext`. |
| **Max variable number dynamic?** | `SQLitePCLRaw` allows `sqlite3_limit` at runtime — could expose `MaxParametersPerCommand` auto-detection via `connection.Handle`? | Keep static 999 for determinism; advanced users can bump via options. |

---

## 9. Verification Checklist (before merge)

- [ ] `BulkProviderRegistry.Resolve("Microsoft.EntityFrameworkCore.Sqlite")` returns non-null when `NSLabs.EFCore.Extensions.Sqlite` referenced (ModuleInitializer fires).
- [ ] Golden-SQL suite for SQLite passes (update/delete/upsert/computed/LIKE/IN/quoting).
- [ ] Chunking with `MaxParametersPerCommand=999` splits identically to SQL Server suite behavior (adjusted budget).
- [ ] Integration suite passes on `DataSource=:memory:` (no Docker): updates, deletes, upserts with guard, computed `+ || COALESCE CASE UPPER`, per-op `RowsAffected` correct, duplicate-key pre-validation throws, discriminator filtering, `ThrowIfZeroAffected` with/without ambient transaction, sequential ordering, entity-style row bulk.
- [ ] Sample `Samples.Sqlite` runs `dotnet run` and shows `Basic`/`Advanced`/`Transaction`/`TableApiAndOptions` logs with SQLite file.
- [ ] Docs updated and `dotnet test -c Release` (unit+integration) green on clean clone.

---

## 10. References

- `docs/DESIGN.md:234` — Strategy A caveat to replace with Sequential wrapped approach.
- `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs` — single source of truth for chunking/emit logic to mirror.
- `src/NSLabs.EFCore.Extensions/Internal/LinqPredicateTranslator.cs` + `SetExpressionTranslator.cs` — shared translators.
- `tests/NSLabs.EFCore.Extensions.Tests.Unit/Harness.cs` / `SqlServerFixture.cs:8` — harness pattern to replicate for SQLite.
- Microsoft.Data.Sqlite docs: parameter limit, batching, `changes()`.

> This plan intentionally enumerates **every** provider-conditional branch (quoting, LIKE escape, CONCAT, LEN, SUBSTR, TRIM, CEIL/FLOOR, ROUND truncate, HOLDLOCK absence, `@@ROWCOUNT` replacement, param budget, `ON CONFLICT` constraint, computed alias) so no feature gap remains. Follow the checklist top-to-bottom; no additional design doc needed.
