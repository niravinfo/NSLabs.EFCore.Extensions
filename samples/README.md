# NSLabs.EFCore.Extensions - Samples

This folder contains sample applications demonstrating all features of NSLabs.EFCore.Extensions. Layout is provider-consistent for future providers.

```bash
dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer
# future: dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.Postgres
```

## Structure

```
samples/
  README.md
  NSLabs.EFCore.Extensions.Samples.Shared/    # Shared domain (Product/Customer/Order) + Scenarios
    Models/ Data/ Scenarios/
  NSLabs.EFCore.Extensions.Samples.SqlServer/ # Thin host: Program.cs + appsettings.json (UseSqlServer)
  // future: NSLabs.EFCore.Extensions.Samples.Postgres/  # UseNpgsql, same Shared
```

### NSLabs.EFCore.Extensions.Samples.Shared
Provider-agnostic scenarios (Basic/Advanced/Transaction/RealWorld/TableApiAndOptions) with isolated run via `DatabaseHelper.ClearAllAsync` — takes `SampleDbContext` + `ILogger`, no `UseSqlServer`. Reused by every provider host.

### NSLabs.EFCore.Extensions.Samples.SqlServer
Thin console host for SQL Server:
- `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.Logging.Console` (no Serilog)
- `AddDbContext<SampleDbContext>(o=>o.UseSqlServer(conn))`
- Isolated `DbContext` per menu choice + `ChangeTracker.Clear()`

### Example Categories

1. **Basic Examples** (5 examples)
   - Simple bulk update
   - Update with predicates
   - Bulk upsert
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

4. **Real-World Examples** (4 examples)
   - Inventory restock workflow
   - Order fulfillment workflow
   - Dynamic pricing by category
   - Customer loyalty batch processing

## 🛠️ Requirements

- **.NET 10 SDK**
- **SQL Server** (LocalDB, Express, or full version)

### Using LocalDB (Recommended for Quick Start)
LocalDB is installed with Visual Studio or can be installed separately. Start it with:
```bash
sqllocaldb start mssqllocaldb
```

### Using Your Own SQL Server
Edit `appsettings.json` in the sample project:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_SERVER;Database=NSLabsSample;..."
  }
}
```

## Documentation

- **SqlServer host**: `NSLabs.EFCore.Extensions.Samples.SqlServer/Program.cs`
- **Shared scenarios**: `NSLabs.EFCore.Extensions.Samples.Shared/Scenarios/`
- **Main Library README**: [../README.md](../README.md)
- **Design Details**: [../docs/DESIGN.md](../docs/DESIGN.md)
- **Transaction Guide**: [../docs/TRANSACTIONS.md](../docs/TRANSACTIONS.md)

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

**Ready to dive in?** Run `dotnet run --project samples/NSLabs.EFCore.Extensions.Samples.SqlServer` and explore via `ILogger` console output.
