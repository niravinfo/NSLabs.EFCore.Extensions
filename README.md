# NSLabs.EFCore.Extensions

Batched conditional bulk update / upsert for Entity Framework Core — execute *N different* `WHERE` + `SET` operations in **one round-trip** with sequential semantics (later ops see earlier writes) and caller-controlled transaction (no implicit transaction by default — see [Transactions](#transactions)).

SQL Server is supported first (batched parameterized script + `MERGE` for upserts). PostgreSQL / MySQL / SQLite providers are planned.

## Why

Standard EF Core:

- `ExecuteUpdateAsync` → 1 filter + 1 payload per call = N round-trips
- `BulkExtensions` / `FlexLabs.Upsert` → PK-only or 1 op per call

This library: `N` heterogeneous `UPDATE` / `UPSERT` / `DELETE` across multiple tables, one `DbCommand`, param-budget chunking (~2100 params on SQL Server), per-op `RowsAffected`.

## Installation

```bash
dotnet add package NSLabs.EFCore.Extensions
dotnet add package NSLabs.EFCore.Extensions.SqlServer
```

Requires `.NET 10` and `Microsoft.EntityFrameworkCore` `10.0.0`.

## Quick start

### Bulk batch (multi-table, single round-trip)

```csharp
var result = await db.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6)
                           .Set(x => x.Key1, "Value1")
                           .Set(x => x.Key2, 5));

    b.Update<Order>(op => op.Where(x => x.Status == OrderStatus.Pending)
                            .Set(x => x.Status, OrderStatus.Shipped));

    b.Upsert<Customer>(u => u.On(x => x.Code)
                             .WhenMatched(x => x.Active)
                             .Values(new Customer { Code = "A", Name = "X" }));
});

// per-op counts (SQL Server)
result.Operations[0].RowsAffected
```

### Simple helper when you only use one table

If all your updates are for the same table, you can use this shorter way. It does the same thing, just less code to write:

```csharp
await db.Items.BulkUpdateAsync(b =>
{
    b.Add(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
    b.Add(op => op.Where(x => x.Key1 == "Old").Set(x => x.Key3, 0));
});

// also works with a list of items
await db.Items.BulkUpdateAsync(new[] { e1, e2 });
```

### Deferred builder

```csharp
IBulkBatch batch = db.CreateBulkBatch();
batch.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
ApplyRules(batch);
var r = await batch.ExecuteAsync(ct);
```

## Documentation

- **[Design & Architecture](docs/DESIGN.md)** — Full semantics, translation pipeline, and provider strategies
- **[Transaction Semantics](docs/TRANSACTIONS.md)** — Detection, `HOLDLOCK`, `ThrowIfZeroAffected` interaction, migration from `AutoTransaction`
- **[Computed SET Support](docs/COMPUTED_SET_SUPPORT.md)** — Computed column expressions in SET clauses
- **[Testing Guide](docs/TESTING.md)** — Test structure and conventions

## Sample Application

Provider-consistent layout for future Postgres/MySql (`Shared` + per-provider host):

- `samples/NSLabs.EFCore.Extensions.Samples.Shared/` — provider-agnostic domain + scenarios (`Basic`/`Advanced`/`Transaction`/`RealWorld`/`TableApiAndOptions`) with isolated database clear before each run
- `samples/NSLabs.EFCore.Extensions.Samples.SqlServer/` — thin host (`Host` + `Microsoft.Extensions.Logging.Console`, `UseSqlServer`)

**Quick start:**
```bash
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer
# Logs via ILogger (AddConsole), isolated DbContext per menu choice
```

## Transactions

Bulk operations **do not create a transaction** (matches `EFCore.BulkExtensions` and EF Core `ExecuteUpdate` — each statement relies on SQL Server's implicit per-statement transaction). For all-or-nothing across multiple ops/chunks or mixed `SaveChanges`, start a transaction yourself:

```csharp
// 1. No transaction (default) — each statement commits individually
var r = await db.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "V1"));
    b.Upsert<Customer>(u => u.On(x => x.Code).Values(customer));
});

// 2. Caller-managed transaction — atomic
await using var tx = await db.Database.BeginTransactionAsync();
try
{
    await db.BulkExecuteAsync(b => { b.Update<Item>(...); b.Delete<AuditLog>(...); });
    await db.SaveChangesAsync(); // optional — participates in same tx
    await tx.CommitAsync();
}
catch { await tx.RollbackAsync(); throw; }

// Raw ADO.NET transaction must be flowed via EF:
await db.Database.UseTransactionAsync(connTx);
await db.BulkExecuteAsync(b => { ... });
```

The executor piggybacks on `Database.CurrentTransaction` (`command.Transaction = CurrentTransaction.GetDbTransaction()`) and never commits/rollbacks itself. `ThrowIfZeroAffected` is validated after all chunks — without a transaction prior chunks are already committed, with a transaction the caller can roll back.

See the [Transaction Semantics](docs/TRANSACTIONS.md) documentation for complete details.

## Repository

https://github.com/niravinfo/NSLabs.EFCore.Extensions

## License

MIT — see [LICENSE](LICENSE).
