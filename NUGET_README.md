# NSLabs.EFCore.Extensions

[![Build](https://github.com/niravinfo/NSLabs.EFCore.Extensions/actions/workflows/build.yml/badge.svg)](https://github.com/niravinfo/NSLabs.EFCore.Extensions/actions/workflows/build.yml)
[![Release](https://github.com/niravinfo/NSLabs.EFCore.Extensions/actions/workflows/release.yml/badge.svg)](https://github.com/niravinfo/NSLabs.EFCore.Extensions/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/NSLabs.EFCore.Extensions.svg)](https://www.nuget.org/packages/NSLabs.EFCore.Extensions/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NSLabs.EFCore.Extensions.svg)](https://www.nuget.org/packages/NSLabs.EFCore.Extensions/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/niravinfo/NSLabs.EFCore.Extensions/blob/main/LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![.NET 11](https://img.shields.io/badge/.NET-11-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/11.0)

Batched conditional bulk update / upsert for Entity Framework Core. It runs *N different* `WHERE` + `SET` operations in one round-trip with sequential semantics (later operations see earlier writes). Transactions are caller-controlled.

## How it compares

With plain EF Core, `ExecuteUpdateAsync` takes one filter and one payload per call, so N operations mean N round-trips. `BulkExtensions` and `FlexLabs.Upsert` are limited to primary-key matching or one operation per call.

This library runs `N` heterogeneous `UPDATE`, `UPSERT` and `DELETE` operations across multiple tables in one `DbCommand`. It chunks large batches to stay under the parameter budget (about 2100 parameters on SQL Server) and reports `RowsAffected` per operation.

## Installation

Targets both `.NET 10` (with `Microsoft.EntityFrameworkCore` `10.x.x`) and `.NET 11` (with `Microsoft.EntityFrameworkCore` `11.x.x`). NuGet selects the matching build automatically.

> **SQL Server compatibility:** EF Core 11 defaults `UseSqlServer` to compatibility level 160 (SQL Server 2022), so `Math.Min`/`Math.Max` inside `Set(...)` translate to `LEAST`/`GREATEST`. EF Core 10 defaults to 150 and throws an actionable exception instead. If your database is older than SQL Server 2022, set `UseCompatibilityLevel(150)` explicitly.

Choose a provider package.

### SQL Server

```bash
dotnet add package NSLabs.EFCore.Extensions.SqlServer
```

### SQLite

```bash
dotnet add package NSLabs.EFCore.Extensions.Sqlite
```

### PostgreSQL

```bash
dotnet add package NSLabs.EFCore.Extensions.Npgsql
```

### MySQL

Not supported yet.

## Quick start

### Bulk batch (multi-table, single round-trip)

```csharp
var today = DateTime.UtcNow.Date;
var result = await db.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6)
                           .Set(x => x.Key1, "Value1")
                           .Set(x => x.Key2, 5));

    b.Update<Order>(op => op.Where(x => x.Status == OrderStatus.Pending)
                            .Set(x => x.Status, OrderStatus.Shipped));

    // Page-view counter: increment existing rows, insert missing ones with Views = 1.
    b.Upsert<DailyArticleViews>(u => u.MatchOn(v => new { v.ArticleId, v.Date })
                                      .Update(v => v.Views, v => v.Views + 1)
                                      .Insert(new DailyArticleViews { ArticleId = 42, Date = today, Views = 1 }));
});

// per-op counts (SQL Server)
result.Operations[0].RowsAffected;
```

### Single-table helper

When every operation targets the same table, use this shorter form:

```csharp
await db.Items.BulkUpdateAsync(b =>
{
    b.Add(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
    b.Add(op => op.Where(x => x.Key1 == "Old").Set(x => x.Key3, 0));
});
```

### Deferred builder

```csharp
IBulkBatch batch = db.CreateBulkBatch();
batch.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
var result = await batch.ExecuteAsync(ct);
```

## Transactions

Bulk operations do not create a transaction. Like `EFCore.BulkExtensions` and EF Core `ExecuteUpdate`, each statement relies on the database's implicit per-statement transaction. If you need all-or-nothing execution across operations or a mix of bulk work and `SaveChanges`, start a transaction yourself:

```csharp
// 1. No transaction (default): each statement commits individually
var reading = new EnergyReading
{
    MeterId = "MTR-1001",
    Date = DateTime.UtcNow.Date,
    ConsumptionKwh = 18.42,
    RecordedAt = DateTime.UtcNow
};

var r = await db.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "V1"));
    b.Upsert<EnergyReading>(u => u.MatchOn(m => new { m.MeterId, m.Date })
                                  .Update(m => m.ConsumptionKwh, reading.ConsumptionKwh)
                                  .Update(m => m.RecordedAt, reading.RecordedAt)
                                  .Insert(reading));
});

// 2. Caller-managed transaction: atomic across all operations
await using var tx = await db.Database.BeginTransactionAsync();
try
{
    await db.BulkExecuteAsync(b => { b.Update<Item>(...); b.Delete<AuditLog>(...); });
    await db.SaveChangesAsync(); // optional: participates in the same transaction
    await tx.CommitAsync();
}
catch { await tx.RollbackAsync(); throw; }

// 3. Use an existing ADO.NET transaction
await db.Database.UseTransactionAsync(connTx);
await db.BulkExecuteAsync(b => { ... });
```

The executor uses `Database.CurrentTransaction` and never commits or rolls back. `ThrowIfZeroAffected` is checked after all chunks. Without a transaction, earlier chunks have already committed; with one, the caller can roll them back.

## Documentation and support

- [Design & Architecture](https://github.com/niravinfo/NSLabs.EFCore.Extensions/blob/main/docs/DESIGN.md)
- [Working examples](https://github.com/niravinfo/NSLabs.EFCore.Extensions/tree/main/samples)
- [GitHub Issues](https://github.com/niravinfo/NSLabs.EFCore.Extensions/issues)
- [Source repository](https://github.com/niravinfo/NSLabs.EFCore.Extensions)

## License

MIT.
