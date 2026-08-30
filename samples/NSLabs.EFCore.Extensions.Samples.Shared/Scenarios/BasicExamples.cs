using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSLabs.EFCore.Extensions.Samples.Data;
using NSLabs.EFCore.Extensions.Samples.Models;

namespace NSLabs.EFCore.Extensions.Samples.Scenarios;

public static class BasicExamples
{
    public static async Task RunAllAsync(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("=== BASIC EXAMPLES ===");
        await DatabaseHelper.ClearAllAsync(db, logger);
        await Example1_SimpleBulkUpdate(db, logger);
        await Example2_BulkUpdateWithPredicate(db, logger);
        await Example3_BulkUpsert(db, logger);
        await Example4_BulkDelete(db, logger);
        await Example5_MultipleSetsOnSameEntity(db, logger);
    }

    private static async Task Example1_SimpleBulkUpdate(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 1: Simple Bulk Update");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var sku = $"LAPTOP-{suffix}";
        var product = new Product
        {
            Sku = sku,
            Name = "Gaming Laptop",
            Price = 1299.99m,
            StockQuantity = 50,
            IsActive = true,
            Category = "Electronics",
            LastRestocked = DateTime.UtcNow
        };

        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created product {Name} Id={Id} Sku={Sku} Price={Price}", product.Name, product.Id, sku, product.Price);

        var result = await db.BulkExecuteAsync(batch =>
        {
            batch.Update<Product>(op => op
                .Where(p => p.Id == product.Id)
                .Set(p => p.Price, 1199.99m)
                .Set(p => p.LastRestocked, DateTime.UtcNow));
        });

        logger.LogInformation("Updated product price. RowsAffected={Total} Operations={Count}", result.TotalRowsAffected, result.Operations.Count);
        foreach (var op in result.Operations)
            logger.LogInformation("Op {Entity} Rows={Rows}", op.EntityType, op.RowsAffected);

        db.ChangeTracker.Clear();
        var updated = await db.Products.AsNoTracking().FirstAsync(p => p.Id == product.Id);
        logger.LogInformation("Verified new price: {Price}", updated.Price);
    }

    private static async Task Example2_BulkUpdateWithPredicate(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 2: Bulk Update with Predicate");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var products = new[]
        {
            new Product { Sku = $"MOUSE-{suffix}", Name = "Wireless Mouse", Price = 29.99m, StockQuantity = 0, IsActive = true, Category = "Accessories", LastRestocked = DateTime.UtcNow },
            new Product { Sku = $"KEYBOARD-{suffix}", Name = "Mechanical Keyboard", Price = 89.99m, StockQuantity = 0, IsActive = true, Category = "Accessories", LastRestocked = DateTime.UtcNow },
            new Product { Sku = $"MONITOR-{suffix}", Name = "4K Monitor", Price = 399.99m, StockQuantity = 0, IsActive = true, Category = "Electronics", LastRestocked = DateTime.UtcNow }
        };

        db.Products.AddRange(products);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created {Count} products with zero stock", products.Length);

        var result = await db.BulkExecuteAsync(batch =>
        {
            batch.Update<Product>(op => op
                .Where(p => p.StockQuantity == 0 && p.Sku.Contains(suffix))
                .Set(p => p.IsActive, false));
        });

        logger.LogInformation("Deactivated out-of-stock products. RowsAffected={Rows}", result.TotalRowsAffected);
        db.ChangeTracker.Clear();
        var inactiveCount = await db.Products.CountAsync(p => !p.IsActive && p.Sku.Contains(suffix));
        logger.LogInformation("Total inactive for this batch: {Count}", inactiveCount);
    }

    private static async Task Example3_BulkUpsert(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 3: Bulk Upsert (Insert or Update)");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var email = $"john.doe.{suffix}@example.com";

        var result1 = await db.BulkExecuteAsync(batch =>
        {
            batch.Upsert<Customer>(op => op
                .On(c => c.Email)
                .Values(new Customer
                {
                    Email = email,
                    Name = "John Doe",
                    IsActive = true,
                    LoyaltyPoints = 0,
                    CreatedAt = DateTime.UtcNow
                }));
        });
        logger.LogInformation("First upsert inserted. RowsAffected={Rows}", result1.TotalRowsAffected);
        db.ChangeTracker.Clear();

        var result2 = await db.BulkExecuteAsync(batch =>
        {
            batch.Upsert<Customer>(op => op
                .On(c => c.Email)
                .Set(c => c.LoyaltyPoints, 100)
                .Values(new Customer
                {
                    Email = email,
                    Name = "John Doe",
                    IsActive = true,
                    LoyaltyPoints = 0,
                    CreatedAt = DateTime.UtcNow
                }));
        });
        logger.LogInformation("Second upsert updated. RowsAffected={Rows}", result2.TotalRowsAffected);
        db.ChangeTracker.Clear();
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Email == email);
        logger.LogInformation("Customer {Name} Points={Points}", customer.Name, customer.LoyaltyPoints);
    }

    private static async Task Example4_BulkDelete(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 4: Bulk Delete");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var anchor = await db.Products.AsNoTracking().FirstOrDefaultAsync();
        var anchorId = anchor?.Id ?? 1;
        var logs = new[]
        {
            new InventoryLog { ProductId = anchorId, Action = "Restock", QuantityChange = 100, NewQuantity = 100, Timestamp = DateTime.UtcNow.AddDays(-90), Notes = suffix },
            new InventoryLog { ProductId = anchorId, Action = "Restock", QuantityChange = 50, NewQuantity = 50, Timestamp = DateTime.UtcNow.AddDays(-95), Notes = suffix },
            new InventoryLog { ProductId = anchorId, Action = "Sale", QuantityChange = -1, NewQuantity = 99, Timestamp = DateTime.UtcNow.AddDays(-5), Notes = suffix }
        };

        db.InventoryLogs.AddRange(logs);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created {Count} inventory logs with suffix {Suffix}", logs.Length, suffix);

        var cutoffDate = DateTime.UtcNow.AddDays(-30);
        var result = await db.BulkExecuteAsync(batch =>
        {
            batch.Delete<InventoryLog>(op => op
                .Where(log => log.Timestamp < cutoffDate && log.Notes == suffix));
        });
        logger.LogInformation("Deleted old logs. RowsAffected={Rows}", result.TotalRowsAffected);
        db.ChangeTracker.Clear();
        var remainingCount = await db.InventoryLogs.CountAsync(x => x.Notes == suffix);
        logger.LogInformation("Remaining logs with suffix: {Count}", remainingCount);
    }

    private static async Task Example5_MultipleSetsOnSameEntity(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 5: Multiple Sets on Same Entity");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var sku = $"TABLET-{suffix}";
        var product = new Product
        {
            Sku = sku,
            Name = "Android Tablet",
            Price = 299.99m,
            StockQuantity = 20,
            IsActive = true,
            Category = "Electronics",
            LastRestocked = DateTime.UtcNow.AddDays(-10)
        };

        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created product {Name} Sku={Sku}", product.Name, sku);

        var result = await db.BulkExecuteAsync(batch =>
        {
            batch.Update<Product>(op => op
                .Where(p => p.Id == product.Id)
                .Set(p => p.Price, 249.99m)
                .Set(p => p.StockQuantity, 30)
                .Set(p => p.IsActive, true)
                .Set(p => p.LastRestocked, DateTime.UtcNow));
        });
        logger.LogInformation("Updated multiple fields. RowsAffected={Rows}", result.TotalRowsAffected);
        db.ChangeTracker.Clear();
        var updated = await db.Products.AsNoTracking().FirstAsync(p => p.Id == product.Id);
        logger.LogInformation("Verified Price={Price} Stock={Stock} Active={Active}", updated.Price, updated.StockQuantity, updated.IsActive);
    }
}
