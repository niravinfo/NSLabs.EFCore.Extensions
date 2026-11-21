# NSLabs.EFCore.Extensions

[![Build](https://github.com/niravinfo/NSLabs.EFCore.Extensions/actions/workflows/build.yml/badge.svg)](https://github.com/niravinfo/NSLabs.EFCore.Extensions/actions/workflows/build.yml)
[![Release](https://github.com/niravinfo/NSLabs.EFCore.Extensions/actions/workflows/release.yml/badge.svg)](https://github.com/niravinfo/NSLabs.EFCore.Extensions/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/NSLabs.EFCore.Extensions.svg)](https://www.nuget.org/packages/NSLabs.EFCore.Extensions/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NSLabs.EFCore.Extensions.svg)](https://www.nuget.org/packages/NSLabs.EFCore.Extensions/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/niravinfo/NSLabs.EFCore.Extensions/blob/main/LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![.NET 11](https://img.shields.io/badge/.NET-11-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/11.0)

Batched conditional bulk update / upsert for Entity Framework Core. It runs *N different* `WHERE` + `SET` operations in one round-trip with sequential semantics (later operations see earlier writes). Transactions are caller-controlled; there is no implicit transaction by default (see [Transactions](#transactions)).

Supported providers are SQL Server (batched parameterized script plus `MERGE` for upserts), SQLite (`INSERT ... ON CONFLICT` upserts with a file-based database) and PostgreSQL (`INSERT ... ON CONFLICT` upserts via Npgsql). MySQL is planned.

## How it compares

With plain EF Core, `ExecuteUpdateAsync` takes one filter and one payload per call, so N operations mean N round-trips. `BulkExtensions` and `FlexLabs.Upsert` are limited to primary-key matching or one operation per call.

This library runs `N` heterogeneous `UPDATE`, `UPSERT` and `DELETE` operations across multiple tables in one `DbCommand`. It chunks large batches to stay under the parameter budget (about 2100 parameters on SQL Server) and reports `RowsAffected` per operation.

## Installation

Install the provider package for your database.

```bash
dotnet add package NSLabs.EFCore.Extensions.SqlServer   # SQL Server provider
dotnet add package NSLabs.EFCore.Extensions.Sqlite      # SQLite provider
dotnet add package NSLabs.EFCore.Extensions.Npgsql      # PostgreSQL provider
```

Targets both `.NET 10` (with `Microsoft.EntityFrameworkCore` `10.x.x`) and `.NET 11` (with `Microsoft.EntityFrameworkCore` `11.x.x`). NuGet selects the matching build automatically.

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

### Atomic (server-side computed) updates

Pass a value *expression* instead of a constant and it is evaluated inside the `UPDATE`. This avoids a separate read and is safe under concurrency:

```csharp
await db.BulkExecuteAsync(b =>
{
    // increment in place: SET "Key2" = ("Key2" + @p0)
    b.Update<Item>(op => op.Where(x => x.Id == 6)
                           .Set(x => x.Key2, x => x.Key2 + 1));

    // arithmetic on current value: SET "Amount" = ("Amount" * @p0)
    b.Update<Order>(op => op.Where(x => x.OrderNo == "O-1")
                            .Set(x => x.Amount, x => x.Amount * 1.1m));
});
```

Computed expressions support arithmetic (`+ - * / %`), string concatenation (`+`), conditionals (`? :`), coalesce (`??`), string methods (`ToUpper/ToLower/Trim/Substring/Replace/Concat`) and `Math` methods (`Abs/Ceiling/Floor/Round/Truncate/Min/Max`), the same set as EF Core `ExecuteUpdate`'s `SetProperty`. `Math.Min` and `Math.Max` translate to `LEAST`/`GREATEST` where supported (SQLite uses `MIN`/`MAX`). On SQL Server they require compatibility level 160 or higher.

### Deferred builder

```csharp
IBulkBatch batch = db.CreateBulkBatch();
batch.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
var r = await batch.ExecuteAsync(ct);
```

## Documentation

See [Design & Architecture](docs/DESIGN.md) for semantics, the translation pipeline and provider strategies.

## Observability (OpenTelemetry)

Bulk execution emits traces through an inbox `ActivitySource`. No library package references `OpenTelemetry.*`, so no extra dependencies are needed.

- Each non-empty batch produces one `BulkExecute` client span and one `BulkExecute.Chunk` child span per database round-trip. Spans nest under the ambient `Activity.Current`.
- Spans contain counts, provider and operation names, and row counts. SQL text is attached only if you opt in; parameter values are never attached.
- Instrumentation options are process-wide and set once at startup. They are separate from `BulkExecuteOptions` and `DbContext` setup:

```csharp
builder.Services.AddNSLabsBulkInstrumentation(o =>
{
    o.EnableChunkSpans = true;      // per-chunk child spans (default true)
    o.RecordException = true;       // "exception" span events (default true)
    o.CaptureCommandText = false;   // db.statement (default false, truncated)
    o.MaxCommandLength = 4000;      // db.statement truncation guard
});
```

Collect the spans with the standard SDK. Sampling is controlled by the SDK sampler:

```csharp
builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddSource(BulkExecuteTelemetryNames.SourceName)); // "NSLabs.EFCore.Extensions"
```

With no listener attached, no spans are created. To try it locally, run `NSLABS_OTEL_CONSOLE=true dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.Sqlite`.

## Sample applications

The samples share a common project plus one host per provider:

- `samples/NSLabs.EFCore.Extensions.Samples.Shared/` holds the provider-agnostic domain and scenarios (`Basic`/`Advanced`/`Transaction`/`RealWorld`/`TableApiAndOptions`). Each run starts by clearing the database.
- `samples/NSLabs.EFCore.Extensions.Samples.SqlServer/` is a minimal host using `Host`, `Microsoft.Extensions.Logging.Console` and `UseSqlServer`.
- `samples/NSLabs.EFCore.Extensions.Samples.Sqlite/` is a minimal host using `UseSqlite` with `Data Source=nsamples.db`.
- `samples/NSLabs.EFCore.Extensions.Samples.Npgsql/` is a minimal host using `UseNpgsql` with `Host=localhost;Port=5432;...` and `postgres:17` via compose.

Run them like this:
```bash
# SQLite (file-based database)
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.Sqlite

# PostgreSQL via Docker (database created by EnsureCreatedAsync)
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.Npgsql/docker-compose.yml up --build
# or against a local PostgreSQL instance:
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.Npgsql

# SQL Server on Windows (LocalDB) or Linux with an environment override; database created by EnsureCreatedAsync
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer

# SQL Server via Docker on Linux, Windows or macOS; only sample logs are shown
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml up --build --attach samples
```

## Transactions

Bulk operations do not create a transaction. Like `EFCore.BulkExtensions` and EF Core `ExecuteUpdate`, each statement relies on the database's implicit per-statement transaction. If you need all-or-nothing behaviour across operations, chunks or a mix of bulk work and `SaveChanges`, start a transaction yourself:

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

// 2. Caller-managed transaction: atomic
await using var tx = await db.Database.BeginTransactionAsync();
try
{
    await db.BulkExecuteAsync(b => { b.Update<Item>(...); b.Delete<AuditLog>(...); });
    await db.SaveChangesAsync(); // optional: participates in the same transaction
    await tx.CommitAsync();
}
catch { await tx.RollbackAsync(); throw; }

// Raw ADO.NET transaction must be flowed via EF:
await db.Database.UseTransactionAsync(connTx);
await db.BulkExecuteAsync(b => { ... });
```

The executor uses `Database.CurrentTransaction` (`command.Transaction = CurrentTransaction.GetDbTransaction()`) and never commits or rolls back. `ThrowIfZeroAffected` is checked after all chunks. Without a transaction, earlier chunks have already committed; with one, the caller can roll them back.

## Repository

[niravinfo/NSLabs.EFCore.Extensions](https://github.com/niravinfo/NSLabs.EFCore.Extensions)

## License

MIT. See [LICENSE](LICENSE).
