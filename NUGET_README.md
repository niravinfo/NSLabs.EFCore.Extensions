# NSLabs.EFCore.Extensions

Batched conditional bulk update / upsert for Entity Framework Core — execute *N different* `WHERE` + `SET` operations in **one round-trip** with sequential semantics (later ops see earlier writes) and caller-controlled transactions.

## Why

Standard EF Core:

- `ExecuteUpdateAsync` → 1 filter + 1 payload per call = N round-trips
- `BulkExtensions` / `FlexLabs.Upsert` → PK-only or 1 op per call

This library: `N` heterogeneous `UPDATE` / `UPSERT` / `DELETE` across multiple tables, one `DbCommand`, param-budget chunking (~2100 params on SQL Server), per-op `RowsAffected`.

## Installation

**Requirements:** `.NET 10` and `Microsoft.EntityFrameworkCore` `10.0.0`

Choose your database provider:

### SQL Server

```bash
dotnet add package NSLabs.EFCore.Extensions
dotnet add package NSLabs.EFCore.Extensions.SqlServer
```

### SQLite

```bash
dotnet add package NSLabs.EFCore.Extensions
dotnet add package NSLabs.EFCore.Extensions.Sqlite
```

### PostgreSQL / MySQL

Coming soon. Provider support is being expanded.

## Quick Start

### Bulk Batch (multi-table, single round-trip)

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

### Simple Helper (single table)

If all your updates are for the same table, you can use this shorter way:

```csharp
await db.Items.BulkUpdateAsync(b =>
{
    b.Add(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
    b.Add(op => op.Where(x => x.Key1 == "Old").Set(x => x.Key3, 0));
});

// also works with a list of items
await db.Items.BulkUpdateAsync(new[] { e1, e2 });
```

### Deferred Builder

```csharp
IBulkBatch batch = db.CreateBulkBatch();
batch.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
ApplyRules(batch);
var result = await batch.ExecuteAsync(ct);
```

## Transactions

Bulk operations **do not create a transaction** by default (matches `EFCore.BulkExtensions` and EF Core `ExecuteUpdate` behavior). For atomic all-or-nothing execution across multiple operations or mixed `SaveChanges`, start a transaction yourself:

```csharp
// 1. No transaction (default) — each statement commits individually
var r = await db.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "V1"));
    b.Upsert<Customer>(u => u.On(x => x.Code).Values(customer));
});

// 2. Caller-managed transaction — atomic across all operations
await using var tx = await db.Database.BeginTransactionAsync();
try
{
    await db.BulkExecuteAsync(b => { b.Update<Item>(...); b.Delete<AuditLog>(...); });
    await db.SaveChangesAsync(); // optional — participates in same transaction
    await tx.CommitAsync();
}
catch { await tx.RollbackAsync(); throw; }

// 3. Integrate with existing ADO.NET transaction
await db.Database.UseTransactionAsync(connTx);
await db.BulkExecuteAsync(b => { ... });
```

The executor piggybacks on `Database.CurrentTransaction` and never commits/rollbacks itself. `ThrowIfZeroAffected` is validated after all chunks — without a transaction, prior chunks are already committed; with a transaction, the caller can roll back.

## Documentation & Support

- **Full Documentation:** [Design & Architecture](https://github.com/niravinfo/NSLabs.EFCore.Extensions/blob/main/docs/DESIGN.md)
- **Sample Code:** [Working Examples](https://github.com/niravinfo/NSLabs.EFCore.Extensions/tree/main/samples)
- **Issues & Feature Requests:** [GitHub Issues](https://github.com/niravinfo/NSLabs.EFCore.Extensions/issues)
- **Source Code:** [GitHub Repository](https://github.com/niravinfo/NSLabs.EFCore.Extensions)

## License

MIT
