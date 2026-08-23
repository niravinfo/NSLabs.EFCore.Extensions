# NSLabs.EFCore.Extensions — Design Document

**Batched conditional bulk update / upsert for Entity Framework Core**

Status: Approved plan (pre-implementation)
Target: SQL Server first; PostgreSQL / MySQL / SQLite to follow

---

## 1. Problem Definition

Standard EF Core tooling does not cover this scenario:

- **NOT** "update 10,000 rows with the same values" — that is what `ExecuteUpdateAsync`
  or staging-table bulk tools do.
- **IS** "execute *N different statements*, each with its own `WHERE` filter and its own
  `SET` payload, in **one database round trip**".

```sql
Op 1: UPDATE Items SET Key1 = 'V1', Key2 = 5        WHERE Id = 6
Op 2: UPDATE Items SET Key1 = 'V1', Key2 = 5, K3 = 0 WHERE Key1 = 'Old' AND Key2 = -1
```

Plus the same concept for **upsert**: N operations, each = payload + conflict/match key
+ optional guard condition.

### Committed Execution Semantics

Operations apply **sequentially in user order**. Later operations see writes made by
earlier operations — identical behavior to sending them one-by-one, minus the round trips.

> This differs from merging everything into one giant `UPDATE ... CASE WHEN ...` statement,
> where every filter sees pre-state. This library intentionally provides sequential semantics.
> Document this loudly.

### Prior-Art Gap

| Tool | Gap vs requirement |
|---|---|
| `ExecuteUpdateAsync` | 1 filter + 1 payload per call → N round trips |
| FlexLabs.Upsert | 1 op per call; no arbitrary-filter updates |
| EFCore.BulkExtensions | PK-only matching for updates; loads data first in some modes |
| linq2db `MergeInto` | Good upsert, but one source-set per call |

Niche: "batch of heterogeneous conditional DML, single round trip, pure EF metadata" — open.

---

## 2. Public API Design

### Primary Surface — Context-Level Multi-Table Batching

Operations may span **multiple tables and operation kinds** (update / upsert / delete)
in **one round trip and one transaction**. Statements execute strictly in submission
order, so callers control FK-safe sequencing exactly like raw SQL.

```csharp
var r = await db.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6)
                           .Set(x => x.Key1, "Value1")
                           .Set(x => x.Key2, 5));      // expression style

    b.Update<Item>(new[] { e1, e2 });                  // entity style, same table

    b.Update<Order>(op => op.Where(x => x.Status == OrderStatus.Pending)
                            .Set(x => x.Status, OrderStatus.Shipped));

    b.Upsert<Customer>(u => u.On(x => x.Code)
                             .WhenMatched(x => x.Active)
                             .Values(new Customer { Code = "A", Name = "X" }));

    b.Delete<AuditLog>(op => op.Where(x => x.Created < cutoff));   // future op type
});
```

Single-table DbSet extensions remain as sugar delegating to the same engine:

```csharp
await db.Items.BulkUpdateAsync(b => { ... });
await db.Items.BulkUpsertAsync(b => { ... });
```

### Deferred Batch Building

For complex business logic, operations can be accumulated step by step across methods
and executed once at the end — one round trip, one transaction, submission order preserved
(order of Add calls = execution order, enabling FK-safe sequences):

```csharp
IBulkBatch batch = db.CreateBulkBatch();

batch.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
ApplyOrderRules(batch);                       // adds more ops, any table
if (auditEnabled)
    batch.Delete<AuditLog>(op => op.Where(x => x.Created < cutoff));

var r = await batch.ExecuteAsync(ct);
```

The inline form (`db.BulkExecuteAsync(b => ...)`) is sugar over this:
create batch → run action → execute.

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

Two value styles are provided because they serve different habits.

### Style A — Expression Based (surgical, primary)

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

### Style B — Entity Instance Based (sugar, full-row semantics)

```csharp
// Partial entities carrying values; matched by PK by default.
await db.Items.BulkUpdateAsync(new[] { e1, e2, e3 });
await db.Items.BulkUpsertAsync(new[] { e1, e2, e3 });

// Custom match instead of PK:
await db.Items.BulkUpdateAsync(
    source: rows,
    match: (row, x) => x.Code == row.Code);
```

### Semantic Contract

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
    b.Add(op => op
        .On(x => new { x.Code })            // conflict target
        .WhenMatched(x => x.Status != 9)    // optional guard
        .Set(x => x.Key1, "V")
        .InsertValues(new Item { Code = "A", Key1 = "V" })); // row for NOT-MATCHED case
});
```

### Result Object

Per-operation affected-row counts are supported on SQL Server in v1 (same round trip).
Other providers fall back to total-only until implemented; capability exposed via
`SupportsPerOperationCounts` flag.

```csharp
public sealed class BulkUpdateResult
{
    public int TotalRowsAffected { get; }
    public IReadOnlyList<OperationResult> Operations { get; }
}

public sealed class OperationResult
{
    public string EntityType { get; }  // e.g. "Item", "Order"
    public int RowsAffected { get; }   // index maps back to global b.* call order
}
```

Multi-table calls share the parameter budget (~2100 on SQL Server) across all operations;
chunk boundaries may fall anywhere in the sequence but all chunks run inside the single
transaction with order preserved.

Use case: `result.Operations[0].RowsAffected == 0` means that specific filter matched
nothing — callers can confirm changes before replying to their users. Pairs with a planned
`throwIfZeroAffected` option.

### Options Bag

- Chunk size (parameter budget)
- Auto-transaction (default true when no ambient transaction)
- `throwIfZeroAffected` per-op verification
- Command timeout
- SQL logging hook

---

## 3. Translation Pipeline

```
Fluent model → Metadata binding → Grouping/Chunking → Provider strategy → DbCommand(s) → Result
```

1. **Metadata binding**: resolve predicate/`.Set()` lambdas against `IModel` for each
   operation's entity type (operations may span tables):
   - Column names with provider quoting
   - Table mapping
   - **Value converters applied to constants** (enums → int, etc.)
   - Store-generated columns rejected/excluded automatically
2. **Validation**:
   - Unknown property → clear exception (never string-interpolated SQL; injection-proof by construction)
   - Duplicate upsert match-keys inside one batch detected **before** execution
     (SQL Server MERGE and PG `ON CONFLICT` both hard-error on "affect row twice")
   - Empty ops → no-op
3. **Inheritance**: TPH gets discriminator filter injected into `WHERE`; TPT blocked with explicit error in v1.
4. **Change tracking**: untouched by default (parity with `ExecuteUpdateAsync`); opt-in flag later.

Execution goes over the context's connection + current transaction (`Database.CurrentTransaction`),
so logging, interceptors, and retry strategies keep working.

---

## 4. Execution Strategies Per Provider

### Strategy A — Batched Parameterized Script (v1 default, works everywhere)

```sql
UPDATE [Items] SET [Key1] = @p0, [Key2] = @p1 WHERE [Id] = @p2;
UPDATE [Items] SET [Key1] = @p3 WHERE [Key1] = @p4 AND [Key2] = @p5;
```

- One `DbCommand`, one round trip; all values are parameters (never interpolated).
- **Chunking** by parameter budget: SQL Server ~2100 params/request, MySQL & Npgsql ~65k
  placeholders. An op with 4 params → ~500 ops/batch on SQL Server. All chunks run inside
  one transaction so atomicity holds.
- Sequential semantics preserved naturally.
- Per-op counts via appended captures streamed back in the same round trip:

```sql
UPDATE [Items] SET [Key1] = @p0 WHERE [Id] = @p1;
SELECT @rc_0 = @@ROWCOUNT;
UPDATE [Items] SET [Key1] = @p2 WHERE [Key1] = @p3 AND [Key2] = @p4;
SELECT @rc_1 = @@ROWCOUNT;
SELECT @rc_0 AS Op0, @rc_1 AS Op1;   -- final single result set read back
```

- SQLite caveat: Microsoft.Data.Sqlite batching support is shaky → run ops sequentially
  in-process (no network cost anyway), wrapped in one transaction.

### Strategy B — Staging Table + Set-Based UPDATE JOIN (phase 2, scale path)

For thousands of ops: group ops by **predicate shape** (same properties/operators),
stage match-columns + set-columns into temp table/TVP, emit:

```sql
UPDATE i SET i.Key1 = o.S_Key1
FROM Items i JOIN #ops o ON i.Key1 = o.M_Key1 AND i.Key2 = o.M_Key2;
```

Constant parameter count regardless of op count. Requires temp-table DDL permissions.
Opt-in mode only.

### Upsert Generation

Grouped by (conflict-target shape, guard shape):

- **SQL Server**:

```sql
MERGE INTO Items WITH (HOLDLOCK) AS t
USING (VALUES (@pk0, @v0, @g0)) AS s(Id, Key1, Guard)
ON t.Id = s.Id
WHEN MATCHED AND t.Status <> s.Guard THEN UPDATE SET Key1 = s.Key1
WHEN NOT MATCHED THEN INSERT (...) VALUES (...);
```

Multiple MERGEs allowed in one batch. HOLDLOCK documented as required for race safety;
MERGE's known bugs noted.

- **PostgreSQL**:

```sql
INSERT INTO items (...) VALUES (...), (...)
ON CONFLICT (code) DO UPDATE SET key1 = EXCLUDED.key1
WHERE items.status <> s.guard;
```

Source becomes subselect when guards present.

- **MySQL/Pomelo**: `INSERT ... ON DUPLICATE KEY UPDATE col = VALUES(col)`; guards only via
  `col = IF(<cond>, VALUES(col), col)` hack or deferred (decide in M4).
- **SQLite**: native `ON CONFLICT DO UPDATE` (3.24+) with `WHERE`.
- Fallback pair for odd cases: `UPDATE ...; INSERT ... SELECT ... WHERE NOT EXISTS(...)`
  in one transaction (race-safety caveats documented).

Guard-false semantics: row exists → leave untouched (correct).

---

## 5. Edge-Case Matrix (test targets)

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
- Rowversion checks — explicit non-goal v1

---

## 6. Repository Layout

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
| **M0** | Solution skeleton: fluent builders (`BulkUpdateBuilder`, `UpsertBuilder`), op model, metadata binder, `IBulkOperationExecutor` abstraction, SqlServer strategy stub, unit test project with golden-SQL snapshot harness |
| **M1** | Batched UPDATE execution on SQL Server (chunking by param budget, ambient transaction reuse, per-op rowcounts) |
| **M2** | Grouped MERGE upsert + duplicate-key pre-validation |
| **M3** | PostgreSQL provider |
| **M4** | MySQL + SQLite providers |
| **M5** | Staging-table fast path, benchmarks, docs polish |

---

## 8. Locked Decisions

| Decision | Choice |
|---|---|
| First provider | SQL Server |
| API styles | Both (expression + entity) |
| Per-op affected counts | Yes for SQL Server v1 (same round trip); capability flag per provider |
| Packaging | One package (`NSLabs.EFCore.Extensions`), all providers embedded |
| NuGet id / namespace | `NSLabs.EFCore.Extensions` |
| Entry points | `db.BulkExecuteAsync(...)` inline; `db.CreateBulkBatch()` deferred builder (accumulate anywhere, execute once); `db.Items.BulkUpdateAsync/BulkUpsertAsync` as single-table sugar |
| Multi-table batching | Yes — one round trip + one transaction across all operations; submission order defines execution and FK-safe sequencing |
