# Transactions

## Summary

Bulk/batch operations in this library **do not create a transaction by default**. This matches the ecosystem:

* **EFCore.BulkExtensions** — `BulkInsert/BulkUpdate/BulkDelete don't use a transaction by default. This is your responsibility to handle.` `BulkSaveChanges` is the exception (internal transaction, like EF `SaveChanges`).
* **Z.EntityFramework.Extensions** — identical: `Bulk Operations such as BulkInsert, BulkUpdate, BulkDelete don't use a transaction by default.`
* **EF Core `ExecuteUpdate`/`ExecuteDelete`** — [Microsoft Learn](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete): `ExecuteUpdate and ExecuteDelete do not implicitly start a transaction when they're invoked. Each ExecuteUpdate call causes a single SQL UPDATE to be sent... each execute within their own transaction. To wrap multiple operations in a single transaction, explicitly start a transaction.`

`SaveChanges` / `BulkSaveChanges` wrapping a single call in a transaction is the only broad exception; single-statement DML relies on SQL Server's implicit per-statement transaction (`SET IMPLICIT_TRANSACTIONS OFF`).

For multi-statement / multi-chunk batches, opt in explicitly when you need all-or-nothing by starting a transaction yourself.

## How detection works

`src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:15` checks the EF Core ambient transaction:

```csharp
if (database.CurrentTransaction is not null)
    return RunAsync(closeConnection: false);
var strategy = database.CreateExecutionStrategy();
if (strategy.RetriesOnFailure)
    return strategy.ExecuteAsync(() => RunAsync(closeConnection: true));
return RunAsync(closeConnection: true);
```

* When `CurrentTransaction != null` the executor **piggybacks** — `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:56` reuses `database.CurrentTransaction.GetDbTransaction()` and `command.Transaction = transaction` `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:73`. It does **not** `COMMIT`/`ROLLBACK`; the caller owns the transaction lifecycle.
* Otherwise the executor runs without an explicit transaction. Each `UPDATE`/`MERGE`/`DELETE` commits via SQL Server's implicit per-statement transaction. A multi-statement / multi-chunk batch without an explicit wrapper commits per statement — same as `ExecuteUpdate` without `BeginTransaction`.
* `ThrowIfZeroAffected` validation happens after all chunks `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:71`. Inside an ambient transaction the caller can roll back on the resulting `BulkZeroRowsAffectedException`; without a transaction prior chunks have already committed.

Raw `SqlConnection.BeginTransaction()` or `System.Transactions.TransactionScope` that was not flowed via `context.Database.UseTransactionAsync(tx)` / `EnlistTransaction` leaves `CurrentTransaction == null` — the library will not see it. Use the `DatabaseFacade` APIs to flow external transactions.

## Options

`src/NSLabs.EFCore.Extensions/BulkExecuteOptions.cs:9`:

```csharp
public sealed class BulkExecuteOptions
{
    public int MaxParametersPerCommand { get; set; } = 2000;
    public bool ThrowIfZeroAffected { get; set; }
    public int? CommandTimeout { get; set; }
    public Action<string>? OnCommandText { get; set; }
}
```

* No `AutoTransaction` — transaction management is fully caller-owned. Use `Database.BeginTransactionAsync()` when you need atomicity across chunks or mixed `SaveChanges`/bulk work.
* Other `BulkExecuteOptions`: `MaxParametersPerCommand`, `ThrowIfZeroAffected`, `CommandTimeout`, `OnCommandText`.

## Usage

```csharp
// 1. No transaction — each statement's implicit tx (default)
// Each statement commits individually; a later failure does not roll back earlier ones.
var r = await db.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "V1"));
    b.Upsert<Customer>(u => u.On(x => x.Code).Values(customer));
});

// 2. Caller-managed transaction — atomic, recommended for multi-op all-or-nothing
//    and for mixing BulkExecute with SaveChanges.
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
// Commit/rollback via connTx — the library piggybacks on CurrentTransaction.

// With deferred batch:
IBulkBatch batch = db.CreateBulkBatch();
batch.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "V1"));
await using var tx2 = await db.Database.BeginTransactionAsync();
try
{
    var result = await batch.ExecuteAsync();
    await tx2.CommitAsync();
}
catch { await tx2.RollbackAsync(); throw; }
```

## How rows-affected interacts with transactions

Per-op counts are `@@ROWCOUNT` captured per statement `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs:130` as `DECLARE @rc{i}; ... SET @rc{i}=@@ROWCOUNT; SELECT @rc0 AS Op0...` and read back in `SqlServerExecutor.ExecuteChunkAsync` `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:108` via `reader.GetInt32(k)` accumulated per `GlobalIndex` `src/NSLabs.EFCore.Extensions/BulkBatch.cs:80`. `ThrowIfZeroAffected` is validated after all chunks.

* **Inside an ambient transaction** — `ThrowIfZeroAffected` throws before the caller commits, so the whole batch can be rolled back.
* **Without a transaction** — the exception still throws, but prior statements have already committed via implicit per-statement transactions and cannot be rolled back. Wrap with `BeginTransactionAsync()` if you need all-or-nothing on zero-match.

## Choosing

| Need | Use |
|---|---|
| Single-statement batch, best perf | default (no transaction) — per-statement implicit tx |
| Multi-chunk / multi-table all-or-nothing | `Database.BeginTransactionAsync()` |
| Mixed `BulkExecute` + `SaveChanges`/queries | explicit `BeginTransactionAsync()` wrapping all |

## Migration note

If you previously used `new BulkExecuteOptions { AutoTransaction = true }`, that property has been removed. Replace it with an explicit transaction:

```csharp
// Before
await db.BulkExecuteAsync(b => { ... }, new BulkExecuteOptions { AutoTransaction = true });

// After
await using var tx = await db.Database.BeginTransactionAsync();
await db.BulkExecuteAsync(b => { ... });
await tx.CommitAsync();
```

Pre-2026-08 default was `AutoTransaction=true`. The library no longer creates an implicit transaction; the caller must start one when atomicity is required.
