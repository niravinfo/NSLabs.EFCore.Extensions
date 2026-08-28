# Transactions

## Summary

Bulk/batch operations in this library **do not create a transaction by default**. This matches the ecosystem:

* **EFCore.BulkExtensions** — `BulkInsert/BulkUpdate/BulkDelete don't use a transaction by default. This is your responsibility to handle.` `BulkSaveChanges` is the exception (internal transaction, like EF `SaveChanges`).
* **Z.EntityFramework.Extensions** — identical: `Bulk Operations such as BulkInsert, BulkUpdate, BulkDelete don't use a transaction by default.`
* **EF Core `ExecuteUpdate`/`ExecuteDelete`** — [Microsoft Learn](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete): `ExecuteUpdate and ExecuteDelete do not implicitly start a transaction when they're invoked. Each ExecuteUpdate call causes a single SQL UPDATE to be sent... each execute within their own transaction. To wrap multiple operations in a single transaction, explicitly start a transaction.`

`SaveChanges` / `BulkSaveChanges` wrapping a single call in a transaction is the only broad exception; single-statement DML relies on SQL Server's implicit per-statement transaction (`SET IMPLICIT_TRANSACTIONS OFF`).

For multi-statement / multi-chunk batches, opt in explicitly when you need all-or-nothing.

## How detection works

`src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:19` checks the EF Core ambient transaction:

```csharp
if (database.CurrentTransaction is not null)
    return RunAsync(ownTransaction: false, closeConnection: false);
if (strategy.RetriesOnFailure)
    return strategy.ExecuteAsync(() => RunAsync(ownTransaction: options.AutoTransaction, closeConnection: true));
return RunAsync(ownTransaction: options.AutoTransaction, closeConnection: true);
```

* When `CurrentTransaction != null` the executor **piggybacks** — `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:56` reuses `database.CurrentTransaction.GetDbTransaction()` and `command.Transaction = transaction` `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:132`. It does **not** `COMMIT`/`ROLLBACK`; the caller owns the transaction lifecycle.
* Otherwise `ownTransaction = BulkExecuteOptions.AutoTransaction`. When `true`, `RunAsync` `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:50` does `OpenConnectionAsync()` + `BeginTransactionAsync()`, runs all chunks on that transaction, validates `ThrowIfZeroAffected` before commit `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:62`, and `CommitAsync`/`RollbackAsync` on failure.

Raw `SqlConnection.BeginTransaction()` or `System.Transactions.TransactionScope` that was not flowed via `context.Database.UseTransactionAsync(tx)` / `EnlistTransaction` leaves `CurrentTransaction == null` — the library will not see it. Use the `DatabaseFacade` APIs to flow external transactions.

## Options

`src/NSLabs.EFCore.Extensions/BulkExecuteOptions.cs:11`:

```csharp
public bool AutoTransaction { get; set; } = false; // default false
```

* `false` (default) — no implicit transaction. Single `UPDATE`/`MERGE`/`DELETE` is protected by SQL Server's implicit single-statement transaction. A multi-statement / multi-chunk batch without an explicit wrapper commits per statement — same as `ExecuteUpdate` without `BeginTransaction`.
* `true` — executor wraps all chunks in one `BEGIN/COMMIT`. Use for all-or-nothing across chunks or sequential semantics that must not partially commit (also covers `ThrowIfZeroAffected` rollback `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:62`). Holds `HOLDLOCK` (`MERGE WITH (HOLDLOCK)` `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs:217`) across the batch — consider lock duration / concurrency impact.
* Ambient `CurrentTransaction` always wins over `AutoTransaction` — `ownTransaction=false` when present.

Other `BulkExecuteOptions`: `MaxParametersPerCommand`, `ThrowIfZeroAffected`, `CommandTimeout`, `OnCommandText`.

## Usage

```csharp
// 1. No transaction — each statement's implicit tx (default, cheapest)
var r = await db.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "V1"));
    b.Upsert<Customer>(u => u.On(x => x.Code).Values(customer));
});

// 2. Caller-managed transaction — atomic, recommended for mixing with SaveChanges or multi-op atomicity
await using var tx = await db.Database.BeginTransactionAsync();
try
{
    await db.BulkExecuteAsync(b => { b.Update<Item>(...); b.Delete<AuditLog>(...); });
    await db.SaveChangesAsync();
    await tx.CommitAsync();
}
catch { await tx.RollbackAsync(); throw; }

// Raw ADO.NET transaction must be flowed via EF:
await using var connTx = await connection.BeginTransactionAsync();
await db.Database.UseTransactionAsync(connTx);
await db.BulkExecuteAsync(b => { ... });

// 3. Per-call opt-in — library-owned transaction
await db.BulkExecuteAsync(b => { ... }, new BulkExecuteOptions { AutoTransaction = true });

// Also available via deferred batch
IBulkBatch batch = db.CreateBulkBatch();
batch.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "V1"));
var result = await batch.ExecuteAsync(new BulkExecuteOptions { AutoTransaction = true });
```

## How rows-affected interacts with transactions

Per-op counts are `@@ROWCOUNT` captured per statement `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs:130` as `DECLARE @rc{i}; ... SET @rc{i}=@@ROWCOUNT; SELECT @rc0 AS Op0...` and read back in `SqlServerExecutor.ExecuteChunkAsync` `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:166` via `reader.GetInt32(k)` accumulated per `GlobalIndex` `src/NSLabs.EFCore.Extensions/BulkBatch.cs:80`. `ThrowIfZeroAffected` validation before commit allows the whole batch to roll back on zero-match.

## Choosing

| Need | Use |
|---|---|
| Single-statement batch, best perf | default `AutoTransaction=false` |
| Multi-chunk / multi-table all-or-nothing | `AutoTransaction=true` or `Database.BeginTransactionAsync()` |
| Mixed `BulkExecute` + `SaveChanges`/queries | explicit `BeginTransactionAsync()` wrapping all |

## Do we need `AutoTransaction` at all? (Open decision — requirements not final)

The library currently keeps `AutoTransaction` as an opt-in convenience even though the default is user-managed, matching the ecosystem. Whether to keep or delete the flag is under evaluation — implementation is deferred until requirements are final.

### Option A — Keep `AutoTransaction=false` opt-in (current code)

* Pros: One-liner atomicity for chunk-split batches without ceremony: `await db.BulkExecuteAsync(b => {...}, new() { AutoTransaction = true })`. piggyback still applies when `CurrentTransaction != null` `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:19` → `ownTransaction=false` wins. Backwards-compatible for callers who already set `AutoTransaction=true`. Low cost — branch at `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:29` only.
* Cons: Two ways to get a transaction (flag vs explicit `BeginTransaction`). Slight API surface to document/test. Can lure callers into library-owned tx when explicit `BeginTransactionAsync` with mixed `SaveChanges` would be clearer.

### Option B — Delete `AutoTransaction`, require explicit `Database.BeginTransactionAsync()` (pure user-managed, like `ExecuteUpdate` docs)

* Pros: Single model — exactly `ExecuteUpdate`/`EFCore.BulkExtensions.Refresh` (`BulkInsert/Update/Delete don't use transaction... your responsibility`). No ambiguity, easier to reason about lock duration / `HOLDLOCK` `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs:217`. Follows strict “caller controls transaction” principle.
* Cons: Breaking change — `new BulkExecuteOptions { AutoTransaction = true }` no longer compiles. Even simple atomic batch requires:
  ```csharp
  await using var tx = await db.Database.BeginTransactionAsync();
  await db.BulkExecuteAsync(b => {...});
  await tx.CommitAsync();
  ```
  Removal means deleting `BulkExecuteOptions.AutoTransaction` `src/NSLabs.EFCore.Extensions/BulkExecuteOptions.cs:11` and the `options.AutoTransaction` branches `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:29,32,39,50`.

### Current status

Code keeps `AutoTransaction=false` opt-in. Doc notes both options. **No deletion will be done until requirements are final** — next step after sign-off: either (A) keep as-is and just clarify docs, or (B) remove flag and update `DESIGN.md`/`README.md`/`Executor` and add a migration snippet.

Ecosystem precedent supports either — `SqlBulkCopyOptions.UseInternalTransaction` is opt-in, but `EFCore.BulkExtensions` default guidance is explicit transactions.

## Migration note

Pre-2026-08 default was `AutoTransaction=true`. If you relied on the implicit all-or-nothing guarantee without an explicit `BeginTransaction`, add `new BulkExecuteOptions { AutoTransaction = true }` per call or wrap with `Database.BeginTransactionAsync()`.
