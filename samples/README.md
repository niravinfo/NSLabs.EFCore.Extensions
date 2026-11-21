# NSLabs.EFCore.Extensions - Samples

Sample applications for NSLabs.EFCore.Extensions. Each provider uses the same layout so new ones can be added easily.

```bash
# SQLite (file-based database): drops and recreates nsamples.db from the current model, then runs all scenarios
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.Sqlite

# Windows (LocalDB) or Linux with an environment override: drops and recreates the database, then runs all scenarios
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer

# Docker (Linux/Windows/macOS): builds the image, starts SQL Server and runs all scenarios; only sample logs are shown
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml up --build --attach samples

# future: dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.Postgres
```

## Structure

```
samples/
  README.md
  NSLabs.EFCore.Extensions.Samples.Shared/    # Shared domain (Product/Customer/Order/DailyArticleViews/EnergyReading) + Scenarios
    Models/ Data/ Scenarios/
  NSLabs.EFCore.Extensions.Samples.SqlServer/ # Minimal host: Program.cs + appsettings.json (UseSqlServer)
    Dockerfile                                # Multi-stage build
    docker-compose.yml                        # mssql + samples (SQL Server only, no root compose)
    .dockerignore
  NSLabs.EFCore.Extensions.Samples.Sqlite/  # Minimal host: Program.cs + appsettings.json (UseSqlite, Data Source=nsamples.db)
  // future: NSLabs.EFCore.Extensions.Samples.Postgres/  # UseNpgsql, same Shared
```

### NSLabs.EFCore.Extensions.Samples.Shared
Provider-agnostic scenarios (Basic/Advanced/Transaction/RealWorld/TableApiAndOptions) run in isolation through `DatabaseHelper.ClearAllAsync`, which takes `SampleDbContext` and `ILogger` and references neither `UseSqlServer` nor `UseSqlite`. Every provider host reuses them.

### NSLabs.EFCore.Extensions.Samples.SqlServer
Minimal console host for SQL Server:
- `Microsoft.Extensions.Hosting` and `Microsoft.Extensions.Logging.Console`
- `AddDbContext<SampleDbContext>(o=>o.UseSqlServer(conn))`
- `Program.cs` calls `EnsureCreatedAsync()` once with no retry. The compose `healthcheck` and `depends_on: service_healthy` are the only wait mechanism.

### Example Categories

1. Basic Examples (5 examples)
   - Simple bulk update
   - Update with predicates
   - Atomic bulk upsert (page-view counter)
   - Bulk delete
   - Multiple sets on same entity

2. Advanced Examples (4 examples)
   - Multi-table batch operations
   - Computed set expressions
   - Conditional updates across tables
   - Sequential semantics

3. Transaction Examples (4 examples)
   - No transaction (default)
   - Explicit transactions
   - Mixed with SaveChanges
   - Rollback on error

4. Real-World Examples (5 examples)
   - Inventory restock workflow
   - Order fulfillment workflow
   - Dynamic pricing by category
   - Customer loyalty batch processing
   - Smart meter sync (upsert batch)

5. Table API and Options (chunking, ThrowIfZeroAffected, CreateBulkBatch)

## Requirements

- .NET 10 or .NET 11 SDK (samples build for both target frameworks)
- SQL Server (LocalDB, Express, Docker or a full installation). The database is dropped and recreated from the current model on every run, so repeats need no extra setup.

### Option 1: LocalDB (Windows, no configuration)

Default `appsettings.json` uses `Server=(localdb)\\mssqllocaldb`. Just run:

```bash
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer
```

If LocalDB not running: `sqllocaldb start mssqllocaldb`

### Option 2: Docker (Windows, Linux or macOS; recommended on Linux)

SQL Server runs in a container and the database is created automatically. Only sample logs are shown, with the `mssql` boot output hidden. To read those logs, attach to the `samples` container:

```bash
# From repo root - only sample logs (mssql logs hidden by --attach, still retrievable via docker logs)
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml up --build --attach samples
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml down -v  # clean up

# Or from the sample folder
cd samples/NSLabs.EFCore.Extensions.Samples.SqlServer
docker compose up --build --attach samples
docker compose down -v

# Or run detached and follow the samples log
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml up --build -d
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml logs -f samples
# Show all logs (for debugging mssql startup):
docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml logs mssql
```

`docker-compose.yml` lives in the `SqlServer` folder and defines two services: `mssql` with a `healthcheck`, and `samples` with `depends_on: service_healthy`. The health check is the only wait mechanism; the app does not retry. `Program.cs:71` calls `EnsureCreatedAsync()` once. Use `--attach samples` or `logs -f samples` to stream only the `ILogger` output, without SQL Server boot noise. The `mssql` logs are still available through `docker compose logs mssql`.

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

- SQL Server host: `NSLabs.EFCore.Extensions.Samples.SqlServer/Program.cs`
- SQLite host: `NSLabs.EFCore.Extensions.Samples.Sqlite/Program.cs`
- Shared scenarios: `NSLabs.EFCore.Extensions.Samples.Shared/Scenarios/`
- Docker files: `samples/NSLabs.EFCore.Extensions.Samples.SqlServer/Dockerfile`, `docker-compose.yml` and `.dockerignore`, all in the SQL Server folder
- Main library README: [../README.md](../README.md)
- Design document: [../docs/DESIGN.md](../docs/DESIGN.md)

## Learning path

1. Start with the Basic examples to learn the API.
2. Move on to the Advanced examples for more complex scenarios.
3. Review the Transaction examples for transaction handling.
4. Explore the Real-World examples for complete applications.

Each example is self-contained and includes setup code, the bulk operation, verification with output, and comments explaining the steps.

## Example code

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

## Contributing

Open an issue if you find a problem or want to add an example.

---

To run the SQL Server sample, use `dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer` or `docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml up --build --attach samples`, then watch the `ILogger` output.
