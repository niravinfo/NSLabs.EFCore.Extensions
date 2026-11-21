# NSLabs.EFCore.Extensions Design Document

Batched conditional bulk update / upsert for Entity Framework Core.

Status: implemented. SQL Server, SQLite and PostgreSQL (Npgsql) providers are shipped, with batched updates and deletes, MERGE / ON CONFLICT upserts, golden-SQL and live integration suites, and samples for all providers.
Target: SQL Server, SQLite and PostgreSQL supported; MySQL to follow.

---

## 1. Problem Definition

Standard EF Core tooling does not cover this scenario:

- Not "update 10,000 rows with the same values". That is what `ExecuteUpdateAsync`
  or staging-table bulk tools do.
- This library: "execute *N different statements*, each with its own `WHERE` filter and its own
  `SET` payload, in one database round trip".

```sql
Op 1: UPDATE Items SET Key1 = 'V1', Key2 = 5        WHERE Id = 6
Op 2: UPDATE Items SET Key1 = 'V1', Key2 = 5, K3 = 0 WHERE Key1 = 'Old' AND Key2 = -1
```

The same idea applies to upserts: N operations, each with a payload, a conflict or match key
and an optional guard condition.

### Execution semantics

Operations apply sequentially in submission order. Later operations see writes made by
earlier ones, matching the behavior of sending them one by one without the extra round trips.

> Unlike one large `UPDATE ... CASE WHEN ...` statement, where every filter sees pre-state,
> this library provides sequential semantics.

### Comparison

| Tool | Gap vs requirement |
|---|---|
| `ExecuteUpdateAsync` | 1 filter + 1 payload per call → N round trips |
| FlexLabs.Upsert | 1 op per call; no arbitrary-filter updates |
| EFCore.BulkExtensions | PK-only matching for updates; loads data first in some modes |
| linq2db `MergeInto` | Good upsert, but one source-set per call |

The uncovered case is a batch of heterogeneous conditional DML in a single round trip using EF metadata.

---

## 2. Public API Design

### Primary API: context-level multi-table batching

Operations can span multiple tables and kinds (update, upsert, delete) in one round trip.
There is no implicit transaction; callers use `Database.BeginTransactionAsync()` when they
need atomicity (see `README.md#transactions`). Statements execute in submission order,
so callers control FK-safe sequencing just as they would with raw SQL.

```csharp
var today = DateTime.UtcNow.Date;
var r = await db.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6)
                           .Set(x => x.Key1, "Value1")
                           .Set(x => x.Key2, 5));      // expression style

    b.Update<Item>(new[] { e1, e2 });                  // entity style, same table

    b.Update<Order>(op => op.Where(x => x.Status == OrderStatus.Pending)
                            .Set(x => x.Status, OrderStatus.Shipped));

    // Page-view counter: increment existing rows, insert missing ones with Views = 1.
    b.Upsert<DailyArticleViews>(u => u.MatchOn(v => new { v.ArticleId, v.Date })
                                      .Update(v => v.Views, v => v.Views + 1)
                                      .Insert(new DailyArticleViews { ArticleId = 42, Date = today, Views = 1 }));

    b.Delete<AuditLog>(op => op.Where(x => x.Created < cutoff));
});
```

Single-table DbSet extensions delegate to the same engine:

```csharp
await db.Items.BulkUpdateAsync(b => { ... });
await db.Items.BulkUpsertAsync(b => { ... });
```

### Deferred batch building

For complex business logic, operations can be accumulated across methods and executed at
the end in one round trip. Submission order is preserved: the order of Add calls is the
execution order, which allows FK-safe sequences. Transactions are caller-controlled
(see `README.md#transactions`):

```csharp
IBulkBatch batch = db.CreateBulkBatch();

batch.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
// ... add more operations here (any table) ...
if (auditEnabled)
    batch.Delete<AuditLog>(op => op.Where(x => x.Created < cutoff));

var r = await batch.ExecuteAsync(ct);
```

The inline form (`db.BulkExecuteAsync(b => ...)`) does the same thing: create a batch,
run the action, then execute.

Deferred-builder design rules:

| Concern | Decision |
|---|---|
| Validation timing | Metadata validated on every Add call (throws at the offending line); SQL generation/chunking deferred to `ExecuteAsync` |
| Reusability | `ExecuteAsync` does not clear the builder; re-execution allowed and naturally idempotent (absolute-value writes) |
| Thread safety | Not thread-safe; single logical unit of work (matches DbContext conventions) |
| Context affinity | Bound to creating `DbContext`; follows its lifetime; never shared across contexts |
| Empty batch | No-op result without a round trip |
| Per-op counts | Global index = insertion order across all Add calls |
| Dup-key detection | At `ExecuteAsync` (O(n²) check avoided on Add); error names type + both op indices |

Two value styles are available.

### Style A: expression based

```csharp
var result = await db.Items.BulkUpdateAsync(b =>
{
    b.Add(op => op
        .Where(x => x.Id == 6)                       // any LINQ predicate
        .Set(x => x.Key1, "Value1")
        .Set(x => x.Key2, 5));

    b.Add(op => op
        .Where(x => x.Key1 == "OldValue" && x.Key2 == -1)
        .Set(x => x.Key3, 0));
}, ct);
```

### Semantic contract

| Aspect | Expression style | Entity style |
|---|---|---|
| Columns written | Only columns named in `.Set()` | Every mapped scalar column except keys/store-generated |
| NULL handling | You choose what you set | NULLs written as NULL (same rule as EF `UpdateRange`) |
| Match key | Any `.Where()` predicate | PK values from instance; custom matcher optional |

### Upsert API

Conflict target defaults to PK; per-op guard optional.

```csharp
await db.Items.BulkUpsertAsync(b =>
{
    // Nightly smart-meter sync: match refreshes only the two feed columns;
    // a missing (meter, day) row inserts the whole reading instead.
    // Optional `.UpdateWhen(...)` gates the matched update only (never the insert).
    b.Add(op => op
        .MatchOn(r => new { r.MeterId, r.Date }) // conflict target (omit → PK); key value comes from the Insert row
        .Update(r => r.ConsumptionKwh, reading.ConsumptionKwh) // matched-write payload, one call per column
        .Update(r => r.RecordedAt, reading.RecordedAt)
        .Insert(reading)); // row for NOT-MATCHED case
});
```

### Result object

Per-operation affected-row counts are returned in the same round trip.
See `BulkExecuteResult` / `OperationResult` in `src/NSLabs.EFCore.Extensions/BulkExecuteResult.cs`.

Multi-table calls share the parameter budget (~2100 on SQL Server) across all operations;
chunk boundaries may fall anywhere in the sequence. When wrapped in an ambient `Database.CurrentTransaction`, all chunks run inside that single transaction with order preserved; otherwise each statement uses its implicit per-statement transaction (see `README.md#transactions`).

Use case: `result.Operations[0].RowsAffected == 0` means that filter matched nothing,
so callers can confirm changes before replying to users. A planned `throwIfZeroAffected`
option builds on this.

### Options

- Chunk size (parameter budget)
- Transaction: caller-managed via `Database.BeginTransactionAsync()` for all-or-nothing; there is no implicit transaction (see `README.md#transactions`)
- `throwIfZeroAffected` per-operation verification (rolls back only inside an ambient transaction)
- Command timeout
- Per-`DbContext` defaults via `UseBulkExecute(...)` (`IDbContextOptionsExtension`); explicit per-call `BulkExecuteOptions` object fully replaces them. Unconfigured → factory defaults (`MaxParametersPerCommand=2000`, `ThrowIfZeroAffected=false`, `CommandTimeout=null`).

---

## 3. Translation pipeline

```
Fluent model → Metadata binding → Grouping/Chunking → Provider strategy → DbCommand(s) → Result
```

1. Metadata binding: resolve predicate and `.Set()` lambdas against `IModel` for each
   operation's entity type (operations may span tables):
   - Column names with provider quoting
   - Table mapping
   - Value converters applied to constants (enums to ints, for example)
   - Store-generated columns rejected or excluded automatically
2. Validation:
   - Unknown property produces a clear exception; SQL is never built by string interpolation
   - Duplicate upsert match-keys inside one batch are detected before execution
     (SQL Server MERGE and PostgreSQL `ON CONFLICT` both fail on affecting the same row twice)
   - Empty operations are a no-op
3. Inheritance: TPH gets a discriminator filter injected into `WHERE`; TPT is blocked with an explicit error in v1.
4. Change tracking: untouched by default, as with `ExecuteUpdateAsync`; an opt-in flag may be added later.

Execution goes over the context's connection + current transaction (`Database.CurrentTransaction`),
so logging, interceptors, and retry strategies keep working.

> Observability note: execution bypasses EF command interceptors (it uses
> `GetDbConnection()` and a raw `DbCommand`), so EF's own command diagnostics do not
> fire for these round-trips. The library emits its own spans: one `BulkExecute` client
> span per batch (opened in `BulkBatch.ExecuteCoreAsync`) and one `BulkExecute.Chunk`
> child span per executed chunk. The three provider executors share one helper.
> Instrumentation is configured process-wide through
> `services.AddNSLabsBulkInstrumentation(...)` or `BulkInstrumentation.Configure`,
> separately from `DbContext` setup and `BulkExecuteOptions`.

---

## 4. Execution strategies per provider

### Strategy A: batched parameterized script (v1 default, works everywhere)

```sql
UPDATE [Items] SET [Key1] = @p0, [Key2] = @p1 WHERE [Id] = @p2;
UPDATE [Items] SET [Key1] = @p3 WHERE [Key1] = @p4 AND [Key2] = @p5;
```

- One `DbCommand`, one round trip; all values are parameters (never interpolated).
- Chunking by parameter budget: SQL Server about 2100 parameters per request, MySQL and Npgsql about 65k placeholders. An operation with 4 parameters allows roughly 500 operations per batch on SQL Server. All chunks run inside a single transaction only when the caller uses an ambient `Database.CurrentTransaction`; otherwise each statement commits on its own (see `README.md#transactions`).
- Sequential semantics follow from the statement order.
- Per-operation counts are returned through appended captures in the same round trip:

```sql
UPDATE [Items] SET [Key1] = @p0 WHERE [Id] = @p1;
SELECT @rc_0 = @@ROWCOUNT;
UPDATE [Items] SET [Key1] = @p2 WHERE [Key1] = @p3 AND [Key2] = @p4;
SELECT @rc_1 = @@ROWCOUNT;
SELECT @rc_0 AS Op0, @rc_1 AS Op1;   -- final single result set read back
```

- SQLite caveat: Microsoft.Data.Sqlite batching support is unreliable, so operations run
  sequentially in-process (there is no network cost) inside one transaction.

### Strategy B: staging table plus set-based UPDATE JOIN (phase 2)

For thousands of operations, group them by predicate shape (same properties and operators),
stage match-columns + set-columns into temp table/TVP, emit:

```sql
UPDATE i SET i.Key1 = o.S_Key1
FROM Items i JOIN #ops o ON i.Key1 = o.M_Key1 AND i.Key2 = o.M_Key2;
```

Constant parameter count regardless of op count. Requires temp-table DDL permissions.
Opt-in mode only.

### Large `IN` lists (all providers)

Large `Contains` filters collapse to one parameter instead of one-per-value
(5000 ids = 1 param, never throws): SQL Server `OPENJSON` above 100 non-null
values, PostgreSQL always `= ANY` (typed array), SQLite `json_each` above 50.
Thresholds are heuristics for shared 2000-parameter chunks versus single queries;
correctness never depends on them. An overflow backstop converts every convertible
`IN` when the operation would exceed the budget.

Nulls (EF10-exact): strip from every payload; branch iff `hasNull && columnNullable`
(`(FAST OR col IS NULL)`; negated `NOT FAST AND col IS NOT NULL`, or
`NOT FAST OR col IS NULL` when null-free); drop silently vs non-null columns;
all-null → `IS NULL`/`IS NOT NULL` (0 params); empty → `1=0`/`1=1`.
Negation distributes (`SqlNotNode(SqlInNode)` only, never a `NOT (…)` wrapper);
`OR`-expansions are parenthesized (parts AND-join bare).

Counting contract: fast = 1, slow = non-null count, empty/all-null = 0.
Counter and emitter recompute the same pure rule, so plans and params agree by
construction. Core carries no provider dialect: neutral plumbing
in core `LargeListHelper`, dialect beside each generator (`SqlServerLargeList`,
`NpgsqlLargeList`; SQLite needs none beyond SQL text).

Deliberate EF10 deviations (all produce the same results): parameterized `IN (@p…)`
for small lists (never inline constants, so queries stay injection-proof and budgets
remain countable); `WITH` whenever the converted type is known exactly and untyped
`OPENJSON` otherwise (`WITH` describes the converted domain); no padding;
our own `@pN` parameter names; `byte[]` element lists stay multi-parameter in v1.

### Upsert generation

Grouped by (conflict-target shape, guard shape):

- SQL Server:

```sql
MERGE INTO Items WITH (HOLDLOCK) AS t
USING (VALUES (@pk0, @v0, @g0)) AS s(Id, Key1, Guard)
ON t.Id = s.Id
WHEN MATCHED AND t.Status <> s.Guard THEN UPDATE SET Key1 = s.Key1
WHEN NOT MATCHED THEN INSERT (...) VALUES (...);
```

Multiple MERGEs allowed in one batch. HOLDLOCK documented as required for race safety;
MERGE's known bugs noted.

- PostgreSQL:

```sql
INSERT INTO items (...) VALUES (...), (...)
ON CONFLICT (code) DO UPDATE SET key1 = EXCLUDED.key1
WHERE items.status <> s.guard;
```

Source becomes subselect when guards present.

- MySQL/Pomelo: `INSERT ... ON DUPLICATE KEY UPDATE col = VALUES(col)`; guards only via
  `col = IF(<cond>, VALUES(col), col)`, or deferred (decide in M4).
- SQLite: native `ON CONFLICT DO UPDATE` (3.24+) with `WHERE`.
- Fallback pair for odd cases: `UPDATE ...; INSERT ... SELECT ... WHERE NOT EXISTS(...)`
  in one transaction when wrapped (race-safety caveats documented).

Guard-false semantics: row exists → leave untouched (correct).

---

## 5. Edge-case matrix (test targets)

- NULL comparisons, unicode, decimals
- Guid / composite / string keys
- Enums + value converters
- Zero-match ops
- Overlapping-filter ordering semantics
- Duplicate upsert keys
- >chunk-size splitting
- Ambient transaction reuse
- Execution-strategy retry wrapping
- Discriminator filtering (TPH)
- Rowversion checks (explicit non-goal for v1)

---

## 6. Repository layout

```
src/
  NSLabs.EFCore.Extensions/          # API, fluent builders, translation, orchestration (provider-neutral)
                               # provider strategy classes embedded (single package decision)
tests/
  NSLabs.EFCore.Extensions.Tests.Unit/         # golden-SQL snapshots per provider
  NSLabs.EFCore.Extensions.Tests.Integration/  # Testcontainers (sqlserver, postgres, mysql) + SQLite in-memory
samples/
```

---

## 7. Milestones

| Milestone | Scope |
|---|---|
| M0 | Solution skeleton: fluent builders (`BulkUpdateBuilder`, `UpsertBuilder`), op model, metadata binder, `IBulkOperationExecutor` abstraction, SqlServer strategy stub, unit test project with golden-SQL snapshot harness |
| M1 | Batched UPDATE execution on SQL Server (chunking by param budget, ambient transaction reuse, per-op rowcounts) |
| M2 | Grouped MERGE upsert and duplicate-key pre-validation |
| M3 | PostgreSQL provider (shipped: `NSLabs.EFCore.Extensions.Npgsql`, `ON CONFLICT` upserts, `65535` param cap, `TRUE` booleans, qualified `"Table"."Col"` guards/computed) |
| M4 | SQLite provider (shipped); MySQL to follow |
| M5 | Staging-table fast path, benchmarks, docs polish |

---

## 8. Locked decisions

| Decision | Choice |
|---|---|
| First provider | SQL Server |
| API styles | Both (expression + entity) |
| Per-op affected counts | Yes for SQL Server v1 (same round trip); capability flag per provider |
| Packaging | One package (`NSLabs.EFCore.Extensions`), all providers embedded |
| NuGet id / namespace | `NSLabs.EFCore.Extensions` |
| Entry points | `db.BulkExecuteAsync(...)` inline; `db.CreateBulkBatch()` deferred builder (accumulate anywhere, execute once); `db.Items.BulkUpdateAsync/BulkUpsertAsync` as single-table sugar |
| Multi-table batching | Yes. One round trip across all operations; submission order defines execution and FK-safe sequencing. The transaction is caller-controlled: there is no implicit transaction, and `Database.CurrentTransaction` is respected (see `README.md#transactions`). |
| Per-provider capability (hard rule) | Ship native support per provider; never hold back all providers because one lacks support. Core builds provider-neutral nodes; each generator emits natively or throws a provider-specific `NotSupportedException` naming the required version or level and how to enable it. No silent cross-dialect rewrites with diverging semantics: for example, `CASE WHEN` is not used as a substitute for `LEAST`, because `LEAST` ignores NULLs while `CASE WHEN` takes the `ELSE` branch. Fail fast instead, as EF Core does. |
