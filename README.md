# NSLabs.EFCore.Extensions

Batched conditional bulk update / upsert for Entity Framework Core — execute *N different* `WHERE` + `SET` operations in **one round-trip** with sequential semantics (later ops see earlier writes) and caller-controlled transaction (no implicit transaction by default — see [Transactions](#transactions)).

SQL Server (batched parameterized script + `MERGE` for upserts), SQLite (`INSERT ... ON CONFLICT` upserts, zero-config file DB), and PostgreSQL (`INSERT ... ON CONFLICT` upserts via Npgsql) are supported. MySQL is planned.

## Why

Standard EF Core:

- `ExecuteUpdateAsync` → 1 filter + 1 payload per call = N round-trips
- `BulkExtensions` / `FlexLabs.Upsert` → PK-only or 1 op per call

This library: `N` heterogeneous `UPDATE` / `UPSERT` / `DELETE` across multiple tables, one `DbCommand`, param-budget chunking (~2100 params on SQL Server), per-op `RowsAffected`.

## Installation

```bash
dotnet add package NSLabs.EFCore.Extensions
dotnet add package NSLabs.EFCore.Extensions.SqlServer   # SQL Server provider
dotnet add package NSLabs.EFCore.Extensions.Sqlite      # SQLite provider
dotnet add package NSLabs.EFCore.Extensions.Npgsql      # PostgreSQL provider
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
result.Operations[0].RowsAffected;
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

### Atomic (server-side computed) updates

Pass a value *expression* instead of a constant and it runs inside the `UPDATE` — no read-modify-write round-trip, safe under concurrency:

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

Supported in computed expressions: arithmetic (`+ - * / %`), string concat (`+`), conditionals (`? :`), coalesce (`??`), string methods (`ToUpper/ToLower/Trim/Substring/Replace/Concat`), `Math` (`Abs/Ceiling/Floor/Round/Truncate`) — same as EF Core `ExecuteUpdate`'s `SetProperty`. Works in upsert `Set(...)` too (applies to the matched-row update).

### Deferred builder

```csharp
IBulkBatch batch = db.CreateBulkBatch();
batch.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
ApplyRules(batch);
var r = await batch.ExecuteAsync(ct);
```

## Documentation

- **[Design & Architecture](docs/DESIGN.md)** — Semantics, translation pipeline, and provider strategies

## Sample Applications

Provider-consistent layout (`Shared` + per-provider host):

- `samples/NSLabs.EFCore.Extensions.Samples.Shared/` — provider-agnostic domain + scenarios (`Basic`/`Advanced`/`Transaction`/`RealWorld`/`TableApiAndOptions`) with isolated database clear before each run
- `samples/NSLabs.EFCore.Extensions.Samples.SqlServer/` — thin host (`Host` + `Microsoft.Extensions.Logging.Console`, `UseSqlServer`)
- `samples/NSLabs.EFCore.Extensions.Samples.Sqlite/` — thin host (`UseSqlite`, `Data Source=nsamples.db`, zero-config)
- `samples/NSLabs.EFCore.Extensions.Samples.Npgsql/` — thin host (`UseNpgsql`, `Host=localhost;Port=5432;...`, `postgres:17` via compose)

**Quick start:**
```bash
# SQLite (zero-config, file DB)
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.Sqlite

# PostgreSQL (Docker, no manual DB setup, DB auto-created via EnsureCreatedAsync)
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.Npgsql/docker-compose.yml up --build
# or host run against local PG:
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.Npgsql

# Windows (LocalDB) or bare Linux with env override - DB auto-created via EnsureCreatedAsync
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer

# One-click Docker (Linux/Windows/Mac) - no manual DB setup, DB auto-created, only sample logs shown
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml up --build --attach samples
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

## Repository

https://github.com/niravinfo/NSLabs.EFCore.Extensions

## License

MIT — see [LICENSE](LICENSE).
