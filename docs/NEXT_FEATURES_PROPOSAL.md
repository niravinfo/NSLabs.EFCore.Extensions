# Next Features Proposal — NSLabs.EFCore.Extensions

Status: **Draft for review** — no code changes yet.
Scope: **Excludes OTEL (already planned) and MySQL support (explicitly out of scope for now).**
Baseline verified against `main` (`fbef33e`): `src/NSLabs.EFCore.Extensions`, `src/*.SqlServer|Sqlite|Npgsql`, `docs/DESIGN.md`, `README.md`.

## 1. Baseline (verified, do not re-state incorrectly)

1. `IBulkBatch` supports exactly:
   `Update(Action<UpdateOperationBuilder>)`,
   `Update(IEnumerable rows)`,
   `Update(IEnumerable rows, match)`,
   `Upsert(Action<UpsertOperationBuilder>)`,
   `Delete(Action<DeleteOperationBuilder>)`,
   `ExecuteAsync(ct)` / `ExecuteAsync(options, ct)` —
   `src/NSLabs.EFCore.Extensions/IBulkBatch.cs`.
2. `DbSet` sugar supports exactly:
   `BulkUpdateAsync(Action<TableUpdateBuilder>)` (+ `options` overload),
   `BulkUpsertAsync(Action<TableUpsertBuilder>)` (+ `options` overload) —
   `src/NSLabs.EFCore.Extensions/BulkBatchExtensions.cs:48-98`.
   There is **no** `DbSet.BulkDeleteAsync`, **no** `DbSet.BulkInsertAsync`,
   **no** direct `BulkUpdateAsync(IEnumerable)` / `BulkUpsertAsync(IEnumerable)` overload.
   Entity-rows go via `TableUpdateBuilder.Add(rows)` / `Add(rows, match)` —
   `BulkBatchExtensions.cs:116-126`.
3. `BulkExecuteOptions` = `MaxParametersPerCommand = 2000`, `ThrowIfZeroAffected = false`,
   `CommandTimeout = null`, plus per-`DbContext` `UseBulkExecute(...)` defaults —
   `BulkExecuteOptions.cs`, `DependencyInjection/BulkExecuteBuilderExtensions.cs`.
4. Result = `BulkExecuteResult { TotalRowsAffected, Operations: OperationResult(EntityType, RowsAffected) }` —
   `BulkExecuteResult.cs`.
5. SQL Server returns per-op `@@ROWCOUNT` in the same round-trip and reads a final
   single-row result set — `SqlServer/Internal/SqlServerExecutor.cs:127-149`.
   Npgsql/SQLite execute per-chunk `ExecuteNonQuery` and accumulate into `counts[idx]` —
   `Npgsql/Internal/NpgsqlExecutor.cs:121-137`, `Sqlite/Internal/SqliteExecutor.cs:121-137`.
   Zero-row upsert chunks are no-ops (`-- zero-row` guard) on Npgsql/SQLite.
6. `ThrowIfZeroAffected` is checked **after all chunks**. Without an ambient transaction
   prior chunks are already committed — `SqlServerExecutor.cs:60-64` (same pattern in
   Npgsql/SQLite `RunAsync`). Caller owns atomicity via `Database.BeginTransactionAsync()`.
7. Change tracking is untouched by default (`DESIGN.md §2`); samples manually call
   `db.ChangeTracker.Clear()` after bulk runs.
8. Predicates support `== != < <= > >= && || !`, `string.Contains/StartsWith/EndsWith/Equals`,
   `string.IsNullOrEmpty/IsNullOrWhiteSpace`, collection `Contains` (IN),
   `EF.Functions.Like` — error text in `Internal/LinqPredicateTranslator.cs:30,276`.
   Computed `SET` supports arithmetic, string concat, `?:`, `??`, string methods, `Math`
   — `README.md:85`.
9. Store-generated columns are rejected on write — `Internal/ModelBinder.cs:87-94`.
   `IsBindableScalar` excludes PKs; `IsInsertBindable` allows non-generated PKs for upsert
   inserts — `ModelBinder.cs:143-156`. Rowversion/concurrency-token guard is explicit
   non-goal v1 — `DESIGN.md §5`.
10. Target is `net10.0` only — `Directory.Build.props:7`.
11. Staging-table / TVP fast-path is explicitly future — `DESIGN.md §4 Strategy B`, `§7 M5`.
12. Doc bug (to fix with §3): `README.md:65` and `DESIGN.md:141-142` show
    `db.Items.BulkUpdateAsync(new[] { e1, e2 })` / `BulkUpsertAsync(new[] {...})`,
    which have no such overload in `BulkBatchExtensions.cs`.

## 2. Proposal 1 [P0] — Pure `BulkInsert` batch + `RETURNING` / `OUTPUT` keys

### Problem
Highest adoption blocker vs `EFCore.BulkExtensions` / `FlexLabs`. Users needing
seed/ingest of N rows must keep a second library. `Upsert.Insert(row)` covers
insert-if-missing only, not append-only insert, and returns no generated keys.

### Proposal
Add first-class insert op reusing chunking (`MaxParametersPerCommand`), provider
quoting/converters (`ModelBinder.ConvertToProvider`), and caller-controlled
transaction semantics:

```csharp
// Context-level, multi-table, one round-trip per chunk
await db.BulkExecuteAsync(b =>
{
    b.Insert<Item>(new[] { e1, e2 });
    b.Insert<Order>(new[] { o1 });
});

// DbSet sugar (symmetric with Update/Upsert)
await db.Items.BulkInsertAsync(new[] { e1, e2 });
await db.Items.BulkInsertAsync(new[] { e1, e2 }, options, ct);
```

Key return (new result type, does not break `BulkExecuteResult`):

```csharp
public sealed record BulkInsertResult
{
    public required int TotalRowsAffected { get; init; }
    public required IReadOnlyList<InsertedRow> Rows { get; init; }
}
public sealed record InsertedRow(string EntityType, object? Key); // Key = PK value(s); composite => object?[]
```

Providers:

* SQL Server: multi-row `INSERT ... VALUES (...), (...)` + `OUTPUT INSERTED.[PK]`.
* Npgsql: `INSERT ... VALUES ... RETURNING`.
* SQLite: `INSERT ... VALUES ... RETURNING` (3.35+; fallback to `last_insert_rowid()` path if needed — decide in design review).

Semantics (locked):

* Insert columns = `IsInsertBindable` set (same rule as upsert inserts). Store-generated
  identity/computed columns are never written; they are returned when provider supports it.
* Order preserved; per-row key maps back to input index.
* Empty input = no-op, no round-trip (same as empty batch today).
* `ThrowIfZeroAffected` applies unchanged.

### Acceptance criteria
* Golden-SQL unit tests per provider (columns, params, `OUTPUT`/`RETURNING`).
* Live integration: 1 row, N rows, multi-table batch, >chunk-size split, identity keys
  returned and mapped to correct input index, empty input no-op.
* Failure when no PK / no insertable columns has clear message (same style as
  `BulkBatch.cs` validation).

### Effort / risk
Medium. Reuses binder/chunk/executor. Risk is key-readback differences per provider —
isolate in provider generators + executors, keep core `BoundOperation` additive
(`BulkOperationKind.Insert`).

## 3. Proposal 2 [P0, cheap] — `DbSet` symmetry + doc fix

### Problem
Asymmetric DX: `Update`/`Upsert` have `DbSet` sugar, `Delete` does not; no
`IEnumerable` shortcuts. Docs promise overloads that do not exist (§1.12).

### Proposal

```csharp
// Delete sugar (mirrors Update sugar)
await db.Items.BulkDeleteAsync(b => { b.Add(op => op.Where(x => x.Id == 6)); });
await db.Items.BulkDeleteAsync(b => { ... }, options, ct);

// Insert sugar (depends on Proposal 1)
await db.Items.BulkInsertAsync(IEnumerable<TEntity> rows, BulkExecuteOptions? options = null, CancellationToken ct = default);

// Entity-rows Upsert shortcut
await db.Items.BulkUpsertAsync(IEnumerable<TEntity> rows, BulkExecuteOptions? options = null, CancellationToken ct = default);
await db.Items.BulkUpsertAsync(IEnumerable<TEntity> rows, Expression<Func<TEntity,TEntity,bool>> match, ...);

// Missing Update IEnumerable direct overloads (what docs already claim)
await db.Items.BulkUpdateAsync(IEnumerable<TEntity> rows, ...);
```

Add `TableDeleteBuilder` mirroring `TableUpdateBuilder`, and `Add(IEnumerable)` on
`TableUpsertBuilder`. All delegate to the same `BulkBatch` engine; no new SQL.

### Acceptance criteria
* Fix `README.md:65`, `NUGET_README.md:76-83`, `DESIGN.md:83-84,124,141-146,366`
  to match shipped overloads.
* Unit tests: delegation order, empty input no-op, `ArgumentNullException` paths.
* No behavior change to existing overloads.

### Effort / risk
Low. Pure API surface + forwarding. Risk is overload-ambiguity with `Action<>` —
verify with `net10` compiler + add analyzer-friendly names if needed.

## 4. Proposal 3 [P0] — Per-op counts parity + `ToSql()` dry-run

### Problem
`result.Operations[i].RowsAffected` is gold-standard on SQL Server only. Npgsql/SQLite
attribute `ExecuteNonQuery` totals per chunk; multi-row upsert chunks and `-- zero-row`
guards makeper-op interpretation fragile. Operators cannot inspect SQL without a live DB.

### Proposal
1. Define and document per-provider count contract:
   `SupportsPerOperationCounts: bool` on `IBulkProvider` (already anticipated in
   `DESIGN.md:180`), surfaced via docs + debug assertion, not a breaking API.
2. Make Npgsql/SQLite emit one statement per op per chunk (already the case for
   upserts) or accumulate explicitly per op index — no silent total-to-single-op attribution.
3. Add dry-run using the existing `Generate()` pipeline (no execution):

```csharp
IReadOnlyList<SqlPreview> sql = batch.ToSql(); // or batch.ToSql(options)
public sealed record SqlPreview(string CommandText, IReadOnlyDictionary<string, object?> Parameters);
```

`ToSql()` validates (dup upsert keys, `Where`/`Set` required) but never opens a connection.

### Acceptance criteria
* Matrix test: same batch on 3 providers → same `TotalRowsAffected` and same per-op
  array (or documented exception with `SupportsPerOperationCounts == false`).
* `ToSql()` golden tests reuse existing snapshot harness; params are redacted-safe
  (names + values, never interpolated into `CommandText`).
* Docs table: provider × per-op counts × chunking unit.

### Effort / risk
Low-medium. Mostly executor/generator hardening + public preview API. No SQL dialect change.

## 5. Proposal 4 [P1] — Optimistic concurrency guard

### Problem
Today bulk writes are last-writer-wins. No `rowversion` / `IsConcurrencyToken`
participation, so concurrent writers silently overwrite each other.

### Proposal (opt-in, default off)
* On `Update`/`Upsert-matched-update`, if entity has concurrency tokens
  (`IProperty.IsConcurrencyToken`), auto-append `AND [Token] = @token` from:
  (a) explicit `.ConcurrencyToken(value)` / entity-row value, or
  (b) new `BulkExecuteOptions.ConcurrencyMode: Ignore | CompareAndThrow`.
* On zero-affected due to token mismatch when mode is `CompareAndThrow`, throw
  `BulkConcurrencyException(opIndex, entityType)` (new type, mirrors
  `BulkZeroRowsAffectedException`), usable inside caller transaction for rollback.
* Never allow `Set(token, ...)` directly unless user opts into manual increment
  (database-generated tokens stay rejected by `EnsureWritable`).

### Acceptance criteria
* Integration: token match → 1 affected; token stale → 0 affected + typed exception
  when enabled, silent 0 when `Ignore` (current behavior preserved).
* Covers SQL Server `rowversion`, Npgsql `xmin`-mapped tokens, SQLite custom tokens
  per provider docs.
* Golden SQL shows token predicate appended after discriminator filter.

### Effort / risk
Medium. Requires `ModelBinder` token discovery + provider predicate rendering.
Keep default `Ignore` so existing callers are unaffected.

## 6. Proposal 5 [P1] — `ChangeTracker` opt-in sync + transaction helper

### Problem
Two recurring footguns:
(a) tracked entities go stale after bulk writes (users must remember `ChangeTracker.Clear()`),
(b) `ThrowIfZeroAffected` without ambient transaction cannot roll back prior chunks.

### Proposal

```csharp
public enum BulkTrackerSync { None = 0, Clear = 1, UpdateValues = 2 } // default None (back-compat)
public sealed class BulkExecuteOptions
{
    // existing: MaxParametersPerCommand, ThrowIfZeroAffected, CommandTimeout
    public BulkTrackerSync TrackerSync { get; set; } = BulkTrackerSync.None;
}

// Convenience atomic wrapper (sugar over BeginTransactionAsync; never implicit)
public static Task<BulkExecuteResult> BulkExecuteInTransactionAsync(
    this DbContext ctx, Action<IBulkBatch> build,
    BulkExecuteOptions? options = null, CancellationToken ct = default);
```

* `Clear` calls `context.ChangeTracker.Clear()` on success.
* `UpdateValues` updates tracked instances for PK-matched writes only; does not attach new rows.
* Helper starts `Database.BeginTransactionAsync()`, executes batch, commits; rolls back on
  `BulkZeroRowsAffectedException` / `BulkConcurrencyException` / any failure. Respects
  existing `CurrentTransaction` (joins it, never nests).

### Acceptance criteria
* Unit: default `None` preserves current behavior; helper joins ambient tx.
* Integration: stale-read repro → `Clear`/`UpdateValues` fixes; zero-affected mid-batch
  with helper → all rolled back; without helper → documented partial commit.
* README Transactions section updated with decision tree.

### Effort / risk
Low-medium. No SQL change. Risk is over-eager tracking sync — keep `None` default.

## 7. Proposal 6 [P1] — Predicate / type coverage, in order

Current gate is `LinqPredicateTranslator` + `SetExpressionTranslator` + `ModelBinder`.
Expand in this order (stop after each if cost/benefit drops):

1. Large `IN` lists: parameter-budget-aware splitting + empty-`IN` (`false`) / null-element semantics + golden tests.
2. Type edges: `DateOnly/TimeOnly/TimeSpan/Guid/decimal/double/enum + ValueConverter` round-trips per provider (SQLite_affinity + Npgsql mappings are the usual breakage).
3. Provider date/time functions: `EF.Functions` date-part / `DateDiff`-equivalents per dialect; throw clear `NotSupportedException` otherwise (current style).
4. Explicit `owned / JSON-column / TPT / global-query-filter / soft-delete` policy:
   either support with tests or fail fast with actionable message. Today TPH injects
   discriminator (`ModelBinder.AddDiscriminatorPart`); TPT is blocked per `DESIGN.md §2`.
   Do not silently ignore filters.

### Acceptance criteria per sub-item
* Golden-SQL + live integration per provider; unsupported constructs keep the current
  explicit `NotSupportedException` listing supported alternatives.

## 8. Proposal 7 [P2] — Scale path + packaging

1. Staging-table / TVP fast path for 1k+ ops as designed in `DESIGN.md §4 Strategy B`
   (opt-in `BulkExecuteOptions.ExecutionMode: BatchedScript | StagingTable`), constant
   param count, grouped by predicate shape. Requires temp-table/DDL permission docs.
2. Benchmarks (`BenchmarkDotNet`): 10 / 100 / 1k / 10k ops × 3 providers, script vs staging,
   chunk-boundary behavior. Publish results in `docs/BENCHMARKS.md`.
3. Multi-target `net8.0;net10.0` (today `net10.0` only). This is the cheapest adoption lift.
   Gate new C# APIs by `#if` if needed; keep EF Core 8/10 compat matrix in CI.

## 9. Sequencing recommendation

1. Proposals 2 + 3 (symmetry, counts contract, `ToSql`, doc fix) — unblocks correct review of the rest.
2. Proposal 1 (`BulkInsert` + keys) — biggest feature, builds on stable counts/preview.
3. Proposal 5 (tracker + tx helper) — removes footguns before wider adoption.
4. Proposal 4 (concurrency) — needs proposals 1–3 settled.
5. Proposal 6 incrementally; Proposal 7 last (needs benchmarks to justify staging).

## 10. Open questions for review

1. `BulkInsertResult.Rows[i].Key` shape for composite keys: `object?[]` vs tuple vs
   `IReadOnlyDictionary<string,object?>`?
2. Should `BulkDeleteAsync`/`BulkInsertAsync` live on `DbSet<>` only, or also on
   `DbContext` (`db.BulkInsertAsync(rows)`)?
3. `ToSql()` return type name/location: `SqlPreview` in core package vs `Internal` exposure?
4. Concurrency default: keep `Ignore` forever, or warn when tokens exist but mode is `Ignore`?
5. Multi-target: `net8.0` required for next minor, or deferred to staging milestone?

## 11. What this document explicitly does not propose

* OTEL tracing/metrics (separately planned).
* MySQL/Pomelo provider (separately deferred).
* Implicit transactions or auto-rollback without caller `BeginTransactionAsync` /
  `BulkExecuteInTransactionAsync` — caller-controlled transaction stays locked.
* Change-tracking write-through by default — stays opt-in.
