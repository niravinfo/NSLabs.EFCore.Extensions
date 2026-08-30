using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSLabs.EFCore.Extensions.Samples.Data;
using NSLabs.EFCore.Extensions.Samples.Models;

namespace NSLabs.EFCore.Extensions.Samples.Scenarios;

public static class AdvancedExamples
{
    public static async Task RunAllAsync(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("=== ADVANCED EXAMPLES ===");
        await DatabaseHelper.ClearAllAsync(db, logger);
        await Example1_MultiTableBatch(db, logger);
        await Example2_ComputedSetExpressions(db, logger);
        await Example3_ConditionalUpdatesAcrossTables(db, logger);
        await Example4_SequentialSemantics(db, logger);
    }

    private static async Task Example1_MultiTableBatch(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 1: Multi-Table Batch Operations");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var product = new Product { Sku = $"PHONE-{suffix}", Name = "Smartphone", Price = 599.99m, StockQuantity = 100, IsActive = true, Category = "Electronics", LastRestocked = DateTime.UtcNow };
        var customer = new Customer { Email = $"jane.{suffix}@example.com", Name = "Jane Smith", IsActive = true, LoyaltyPoints = 50, CreatedAt = DateTime.UtcNow };
        db.Products.Add(product);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Use actual IDs — do not hardcode 1
        var order = new Order { OrderNumber = $"ORD-2026-{suffix}", CustomerId = customer.Id, OrderDate = DateTime.UtcNow, Status = OrderStatus.Pending, TotalAmount = 599.99m };
        var log = new InventoryLog { ProductId = product.Id, Action = "Sale", QuantityChange = -1, NewQuantity = 99, Timestamp = DateTime.UtcNow.AddDays(-100), Notes = suffix };
        db.Orders.Add(order);
        db.InventoryLogs.Add(log);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created product Id={PId} Sku={Sku} customer Id={CId} order {Order}", product.Id, product.Sku, customer.Id, order.OrderNumber);

        var result = await db.BulkExecuteAsync(batch =>
        {
            batch.Update<Product>(op => op.Where(p => p.Sku == product.Sku).Set(p => p.StockQuantity, 95));
            batch.Update<Customer>(op => op.Where(c => c.Email == customer.Email).Set(c => c.LoyaltyPoints, 100));
            batch.Update<Order>(op => op.Where(o => o.OrderNumber == order.OrderNumber).Set(o => o.Status, OrderStatus.Shipped).Set(o => o.ShippedDate, DateTime.UtcNow));
            batch.Delete<InventoryLog>(op => op.Where(l => l.Notes == suffix && l.Timestamp < DateTime.UtcNow.AddDays(-90)));
        });

        logger.LogInformation("Batch completed Total={Total} Ops={Count}", result.TotalRowsAffected, result.Operations.Count);
        foreach (var op in result.Operations) logger.LogInformation("Op {Entity} Rows={Rows}", op.EntityType, op.RowsAffected);
        db.ChangeTracker.Clear();
    }

    private static async Task Example2_ComputedSetExpressions(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 2: Computed Set Expressions");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var category = $"Audio-{suffix}";
        var products = new[]
        {
            new Product { Sku = $"HEADPHONE-{suffix}", Name = "Wireless Headphones", Price = 79.99m, StockQuantity = 50, IsActive = true, Category = category, LastRestocked = DateTime.UtcNow },
            new Product { Sku = $"SPEAKER-{suffix}", Name = "Bluetooth Speaker", Price = 49.99m, StockQuantity = 30, IsActive = true, Category = category, LastRestocked = DateTime.UtcNow }
        };
        db.Products.AddRange(products);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created audio products category={Category} avgPrice={Avg}", category, products.Average(p => p.Price));

        var result = await db.BulkExecuteAsync(batch =>
        {
            batch.Update<Product>(op => op.Where(p => p.Category == category).Set(p => p.Price, p => p.Price * 0.9m));
        });
        logger.LogInformation("Applied 10% discount Rows={Rows}", result.TotalRowsAffected);
        db.ChangeTracker.Clear();
        var updated = await db.Products.AsNoTracking().Where(p => p.Category == category).ToListAsync();
        foreach (var p in updated) logger.LogInformation("{Name}: {Price}", p.Name, p.Price);
    }

    private static async Task Example3_ConditionalUpdatesAcrossTables(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 3: Conditional Updates Across Tables");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var vip1Email = $"vip1.{suffix}@example.com";
        var vip2Email = $"vip2.{suffix}@example.com";
        var regularEmail = $"regular.{suffix}@example.com";
        var customers = new[]
        {
            new Customer { Email = vip1Email, Name = "VIP Customer 1", IsActive = true, LoyaltyPoints = 500, CreatedAt = DateTime.UtcNow.AddYears(-2) },
            new Customer { Email = vip2Email, Name = "VIP Customer 2", IsActive = true, LoyaltyPoints = 750, CreatedAt = DateTime.UtcNow.AddYears(-3) },
            new Customer { Email = regularEmail, Name = "Regular Customer", IsActive = true, LoyaltyPoints = 50, CreatedAt = DateTime.UtcNow.AddMonths(-6) }
        };
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created {Count} customers with suffix {Suffix}", customers.Length, suffix);

        var result = await db.BulkExecuteAsync(batch =>
        {
            batch.Update<Customer>(op => op.Where(c => (c.Email == vip1Email || c.Email == vip2Email || c.Email == regularEmail) && c.LoyaltyPoints > 500 && c.IsActive).Set(c => c.LoyaltyPoints, c => c.LoyaltyPoints + 100));
            batch.Update<Customer>(op => op.Where(c => (c.Email == vip1Email || c.Email == vip2Email || c.Email == regularEmail) && c.LoyaltyPoints < 100 && c.LastOrderDate == null).Set(c => c.IsActive, false));
        });
        logger.LogInformation("Processed loyalty ops={Count}", result.Operations.Count);
        foreach (var op in result.Operations) logger.LogInformation("Rows={Rows}", op.RowsAffected);
        db.ChangeTracker.Clear();
        var vipCount = await db.Customers.CountAsync(c => c.Email.Contains(suffix) && c.LoyaltyPoints > 600);
        var inactiveCount = await db.Customers.CountAsync(c => c.Email.Contains(suffix) && !c.IsActive);
        logger.LogInformation("VIP>600={Vip} Inactive={Inactive}", vipCount, inactiveCount);
    }

    private static async Task Example4_SequentialSemantics(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 4: Sequential Semantics");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var sku = $"CAMERA-{suffix}";
        var product = new Product { Sku = sku, Name = "Digital Camera", Price = 499.99m, StockQuantity = 100, IsActive = true, Category = "Electronics", LastRestocked = DateTime.UtcNow };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created {Name} Stock={Stock} Sku={Sku}", product.Name, product.StockQuantity, sku);

        var result = await db.BulkExecuteAsync(batch =>
        {
            batch.Update<Product>(op => op.Where(p => p.Sku == sku).Set(p => p.StockQuantity, p => p.StockQuantity - 10));
            batch.Update<Product>(op => op.Where(p => p.Sku == sku && p.StockQuantity < 100).Set(p => p.IsActive, false));
        });
        logger.LogInformation("Sequential ops={Count}", result.Operations.Count);
        db.ChangeTracker.Clear();
        var updated = await db.Products.AsNoTracking().FirstAsync(p => p.Sku == sku);
        logger.LogInformation("Final Stock={Stock} Active={Active}", updated.StockQuantity, updated.IsActive);
    }
}
