# NSLabs.EFCore.Extensions - Samples

This folder contains sample applications demonstrating all features of NSLabs.EFCore.Extensions. Layout is provider-consistent for future providers.

```bash
# SQLite (zero-config, file DB) - drops + recreates nsamples.db from the current model, then runs all scenarios
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.Sqlite

# Windows (LocalDB) or Linux with env override - drops + recreates the DB, then runs all scenarios
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer

# One-click Docker (Linux/Windows/Mac) - builds image, starts SQL Server, runs all scenarios (only sample logs)
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml up --build --attach samples

# future: dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.Postgres
```

## Structure

```
samples/
  README.md
  NSLabs.EFCore.Extensions.Samples.Shared/    # Shared domain (Product/Customer/Order/DailyArticleViews/EnergyReading) + Scenarios
    Models/ Data/ Scenarios/
  NSLabs.EFCore.Extensions.Samples.SqlServer/ # Thin host: Program.cs + appsettings.json (UseSqlServer)
    Dockerfile                                # Multi-stage build
    docker-compose.yml                        # mssql + samples (SQL Server only, no root compose)
    .dockerignore
  NSLabs.EFCore.Extensions.Samples.Sqlite/  # Thin host: Program.cs + appsettings.json (UseSqlite, Data Source=nsamples.db)
  // future: NSLabs.EFCore.Extensions.Samples.Postgres/  # UseNpgsql, same Shared
```

### NSLabs.EFCore.Extensions.Samples.Shared
Provider-agnostic scenarios (Basic/Advanced/Transaction/RealWorld/TableApiAndOptions) with isolated run via `DatabaseHelper.ClearAllAsync` — takes `SampleDbContext` + `ILogger`, no `UseSqlServer`/`UseSqlite`. Reused by every provider host.

### NSLabs.EFCore.Extensions.Samples.SqlServer
Thin console host for SQL Server:
- `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.Logging.Console`
- `AddDbContext<SampleDbContext>(o=>o.UseSqlServer(conn))`
- `Program.cs` does single `EnsureCreatedAsync()` (no retry) - compose `healthcheck + depends_on: service_healthy` is the single wait source (Option A)

### Example Categories

1. **Basic Examples** (5 examples)
   - Simple bulk update
   - Update with predicates
   - Atomic bulk upsert (page-view counter)
   - Bulk delete
   - Multiple sets on same entity

2. **Advanced Examples** (4 examples)
   - Multi-table batch operations
   - Computed set expressions
   - Conditional updates across tables
   - Sequential semantics

3. **Transaction Examples** (4 examples)
   - No transaction (default)
   - Explicit transactions
   - Mixed with SaveChanges
   - Rollback on error

4. **Real-World Examples** (5 examples)
   - Inventory restock workflow
   - Order fulfillment workflow
   - Dynamic pricing by category
   - Customer loyalty batch processing
   - Smart meter sync (upsert batch)

5. **Table API and Options** (chunking, ThrowIfZeroAffected, CreateBulkBatch)

## 🛠️ Requirements

- **.NET 10 SDK**
- **SQL Server** (LocalDB, Express, Docker, or full version) - DB is dropped + recreated from the current model on every run (rerunnable with zero setup)

### Option 1: LocalDB (Windows - zero config)

Default `appsettings.json` uses `Server=(localdb)\\mssqllocaldb`. Just run:

```bash
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer
```

If LocalDB not running: `sqllocaldb start mssqllocaldb`

### Option 2: Docker (Windows / Linux / Mac - recommended for Linux)

No manual DB setup. SQL Server runs in container, DB auto-created. Only **sample logs** are shown - get logs directly from the `samples` container (cleanest, `mssql` boot noise hidden):

```bash
# From repo root - only sample logs (mssql logs hidden by --attach, still retrievable via docker logs)
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml up --build --attach samples
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml down -v  # clean up

# Or from the sample folder
cd samples/NSLabs.EFCore.Extensions.Samples.SqlServer
docker compose up --build --attach samples
docker compose down -v

# Alternative cleanest: run detached and fetch logs from container itself
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml up --build -d
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml logs -f samples
# Show all logs (for debugging mssql startup):
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml logs mssql
```

`docker-compose.yml` (inside `SqlServer` folder, SQL Server only) provides `mssql` with `healthcheck` and `samples` with `depends_on: service_healthy` (single wait source, no app retry). `Program.cs:71` does single `EnsureCreatedAsync()` - Docker `healthcheck` is the only wait; `--attach samples` or `logs -f samples` streams only `samples` (`ILogger`) directly from the container without SQL Server boot noise, while `mssql` logs remain retrievable via `docker compose logs mssql`.

### Option 3: Your Own SQL Server / Bare Linux

Override connection string via env (works on Windows + Linux):

```bash
# Linux bare metal
export ConnectionStrings__DefaultConnection="Server=localhost,1433;Database=NSLabsBulkExtensionsSample;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=True"
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer

# Or inline
ConnectionStrings__DefaultConnection="Server=YOUR_SERVER;Database=NSLabsSample;..." dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer
```

### Editing appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_SERVER;Database=NSLabsSample;..."
  }
}
```

## Documentation

- **SqlServer host**: `NSLabs.EFCore.Extensions.Samples.SqlServer/Program.cs`
- **Sqlite host**: `NSLabs.EFCore.Extensions.Samples.Sqlite/Program.cs`
- **Shared scenarios**: `NSLabs.EFCore.Extensions.Samples.Shared/Scenarios/`
- **Docker**: `samples/NSLabs.EFCore.Extensions.Samples.SqlServer/Dockerfile` + `docker-compose.yml` + `.dockerignore` (all co-located, SQL Server only)
- **Main Library README**: [../README.md](../README.md)
- **Design Details**: [../docs/DESIGN.md](../docs/DESIGN.md)

## 💡 Learning Path

1. **Start with Basic Examples** - Understand the fundamentals
2. **Move to Advanced Examples** - Learn complex scenarios
3. **Review Transaction Examples** - Master transaction handling
4. **Explore Real-World Examples** - See practical applications

Each example is self-contained and includes:
- Setup code (creating test data)
- Bulk operation execution
- Verification and output
- Detailed comments

## 🎓 Example Code Preview

### Simple Update
```csharp
var result = await db.BulkExecuteAsync(batch =>
{
    batch.Update<Product>(op => op
        .Where(p => p.Id == productId)
        .Set(p => p.Price, 99.99m));
});
```

### Multi-Table Batch
```csharp
await db.BulkExecuteAsync(batch =>
{
    batch.Update<Product>(op => op.Where(p => p.Sku == "PROD-001")
                                   .Set(p => p.StockQuantity, p => p.StockQuantity - 5));
    
    batch.Update<Order>(op => op.Where(o => o.OrderNumber == "ORD-001")
                                .Set(o => o.Status, OrderStatus.Shipped));
});
```

### With Transaction
```csharp
await using var tx = await db.Database.BeginTransactionAsync();
try
{
    await db.BulkExecuteAsync(batch => { /* operations */ });
    await tx.CommitAsync();
}
catch { await tx.RollbackAsync(); throw; }
```

## 🤝 Contributing

Found an issue or have an idea for a new example? Contributions welcome!

---

**Ready to dive in?** `dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer` or `docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml up --build --attach samples` and watch `ILogger` console output (only sample logs).
