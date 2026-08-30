using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSLabs.EFCore.Extensions.Samples.Data;
using NSLabs.EFCore.Extensions.Samples.Models;

namespace NSLabs.EFCore.Extensions.Samples.Scenarios;

public static class TransactionExamples
{
    public static async Task RunAllAsync(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("=== TRANSACTION EXAMPLES ===");
        await DatabaseHelper.ClearAllAsync(db, logger);
        await Example1_NoTransactionDefault(db, logger);
        await Example2_ExplicitTransaction(db, logger);
        await Example3_MixedWithSaveChanges(db, logger);
        await Example4_RollbackOnError(db, logger);
    }

    private static async Task Example1_NoTransactionDefault(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 1: No Transaction (Default)");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var sku1 = $"NOTX1-{suffix}";
        var sku2 = $"NOTX2-{suffix}";
        var products = new[]
        {
            new Product { Sku = sku1, Name = "Product 1", Price = 10m, StockQuantity = 10, IsActive = true, Category = "Test", LastRestocked = DateTime.UtcNow },
            new Product { Sku = sku2, Name = "Product 2", Price = 20m, StockQuantity = 20, IsActive = true, Category = "Test", LastRestocked = DateTime.UtcNow }
        };
        db.Products.AddRange(products);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created 2 test products suffix {Suffix}", suffix);

        var result = await db.BulkExecuteAsync(batch =>
        {
            batch.Update<Product>(op => op.Where(p => p.Sku == sku1).Set(p => p.Price, 15m));
            batch.Update<Product>(op => op.Where(p => p.Sku == sku2).Set(p => p.Price, 25m));
        });
        logger.LogInformation("Updates completed without explicit transaction Rows={Rows} Note=implicit per-statement tx", result.TotalRowsAffected);
        db.ChangeTracker.Clear();
    }

    private static async Task Example2_ExplicitTransaction(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 2: Explicit Transaction All-or-Nothing");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var sku = $"TX-{suffix}";
        var email = $"tx-customer.{suffix}@example.com";
        var product = new Product { Sku = sku, Name = "Transactional Product", Price = 100m, StockQuantity = 50, IsActive = true, Category = "Test", LastRestocked = DateTime.UtcNow };
        var customer = new Customer { Email = email, Name = "Transactional Customer", IsActive = true, LoyaltyPoints = 0, CreatedAt = DateTime.UtcNow };
        db.Products.Add(product);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created product {Sku} customer {Email}", sku, email);

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            logger.LogInformation("Transaction started");
            var result = await db.BulkExecuteAsync(batch =>
            {
                batch.Update<Product>(op => op.Where(p => p.Sku == sku).Set(p => p.StockQuantity, p => p.StockQuantity - 5));
                batch.Update<Customer>(op => op.Where(c => c.Email == email).Set(c => c.LoyaltyPoints, c => c.LoyaltyPoints + 10));
            });
            logger.LogInformation("Executed {Count} ops", result.Operations.Count);
            await transaction.CommitAsync();
            logger.LogInformation("Transaction committed Rows={Rows}", result.TotalRowsAffected);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "Transaction rolled back");
        }
        db.ChangeTracker.Clear();
        var updatedProduct = await db.Products.AsNoTracking().FirstAsync(p => p.Sku == sku);
        var updatedCustomer = await db.Customers.AsNoTracking().FirstAsync(c => c.Email == email);
        logger.LogInformation("Final stock={Stock} points={Points}", updatedProduct.StockQuantity, updatedCustomer.LoyaltyPoints);
    }

    private static async Task Example3_MixedWithSaveChanges(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 3: Mixed Bulk + SaveChanges");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var sku = $"MIX-{suffix}";
        var custEmail = $"mixcust.{suffix}@example.com";
        var cust = new Customer { Email = custEmail, Name = "Mix Customer", IsActive = true, LoyaltyPoints = 0, CreatedAt = DateTime.UtcNow };
        db.Customers.Add(cust);
        var product = new Product { Sku = sku, Name = "Mixed Operation Product", Price = 50m, StockQuantity = 100, IsActive = true, Category = "Test", LastRestocked = DateTime.UtcNow };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created product {Sku} customer Id={CId}", sku, cust.Id);

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var bulkResult = await db.BulkExecuteAsync(batch => { batch.Update<Product>(op => op.Where(p => p.Sku == sku).Set(p => p.Price, 45m)); });
            logger.LogInformation("Bulk update 1 Rows={Rows}", bulkResult.TotalRowsAffected);
            var order = new Order { OrderNumber = $"MIX-ORDER-{suffix}", CustomerId = cust.Id, OrderDate = DateTime.UtcNow, Status = OrderStatus.Pending, TotalAmount = 45m };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            logger.LogInformation("SaveChanges order created {Order}", order.OrderNumber);
            var bulkResult2 = await db.BulkExecuteAsync(batch => { batch.Update<Product>(op => op.Where(p => p.Sku == sku).Set(p => p.StockQuantity, p => p.StockQuantity - 1)); });
            logger.LogInformation("Bulk update 2 Rows={Rows}", bulkResult2.TotalRowsAffected);
            await transaction.CommitAsync();
            logger.LogInformation("All ops committed atomically");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "Transaction rolled back");
        }
        db.ChangeTracker.Clear();
    }

    private static async Task Example4_RollbackOnError(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 4: Rollback on Error");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var sku = $"ROLLBACK-{suffix}";
        var product = new Product { Sku = sku, Name = "Rollback Test Product", Price = 100m, StockQuantity = 10, IsActive = true, Category = "Test", LastRestocked = DateTime.UtcNow };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var initialStock = 10;
        logger.LogInformation("Created {Sku} stock={Stock}", sku, initialStock);

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var result1 = await db.BulkExecuteAsync(batch => { batch.Update<Product>(op => op.Where(p => p.Sku == sku).Set(p => p.StockQuantity, 5)); });
            logger.LogInformation("First update rows={Rows}", result1.TotalRowsAffected);
            _ = await db.BulkExecuteAsync(batch => { batch.Update<Product>(op => op.Where(p => p.Id == 999999).Set(p => p.Price, 0m)); });
            throw new InvalidOperationException("Simulated error - rolling back transaction");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogWarning("Error {Message} transaction rolled back", ex.Message);
        }
        db.ChangeTracker.Clear();
        var verify = await db.Products.AsNoTracking().FirstAsync(p => p.Sku == sku);
        logger.LogInformation("Stock after rollback={Stock} expected={Expected}", verify.StockQuantity, initialStock);
    }
}
