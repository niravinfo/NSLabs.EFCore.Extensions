using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSLabs.EFCore.Extensions.Samples.Data;
using NSLabs.EFCore.Extensions.Samples.Models;

namespace NSLabs.EFCore.Extensions.Samples.Scenarios;

public static class RealWorldExamples
{
    public static async Task RunAllAsync(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("=== REAL WORLD EXAMPLES ===");
        await DatabaseHelper.ClearAllAsync(db, logger);
        await Example1_InventoryRestock(db, logger);
        await Example2_OrderFulfillment(db, logger);
        await Example3_PriceUpdate(db, logger);
        await Example4_CustomerLoyaltyBatch(db, logger);
    }

    private static async Task Example1_InventoryRestock(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 1: Inventory Restock Workflow");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var skus = new[] { $"INV1-{suffix}", $"INV2-{suffix}", $"INV3-{suffix}" };
        var products = new[]
        {
            new Product { Sku = skus[0], Name = "Laptop Pro", Price = 1299m, StockQuantity = 5, IsActive = false, Category = "Electronics", LastRestocked = DateTime.UtcNow.AddMonths(-1) },
            new Product { Sku = skus[1], Name = "Mouse Deluxe", Price = 49m, StockQuantity = 3, IsActive = false, Category = "Accessories", LastRestocked = DateTime.UtcNow.AddMonths(-1) },
            new Product { Sku = skus[2], Name = "USB-C Cable", Price = 15m, StockQuantity = 8, IsActive = false, Category = "Accessories", LastRestocked = DateTime.UtcNow.AddMonths(-1) }
        };
        db.Products.AddRange(products);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created {Count} products suffix={Suffix}", products.Length, suffix);

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var restockDate = DateTime.UtcNow;
            var restockMap = new Dictionary<string, int> { [skus[0]] = 50, [skus[1]] = 100, [skus[2]] = 200 };
            var result = await db.BulkExecuteAsync(batch =>
            {
                foreach (var (sku, qty) in restockMap)
                    batch.Update<Product>(op => op.Where(p => p.Sku == sku).Set(p => p.StockQuantity, p => p.StockQuantity + qty).Set(p => p.IsActive, true).Set(p => p.LastRestocked, restockDate));
            });
            db.ChangeTracker.Clear();
            foreach (var (sku, qty) in restockMap)
            {
                var p = await db.Products.AsNoTracking().FirstAsync(x => x.Sku == sku);
                db.InventoryLogs.Add(new InventoryLog { ProductId = p.Id, Action = "Restock", QuantityChange = qty, NewQuantity = p.StockQuantity, Timestamp = restockDate, Notes = suffix });
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            logger.LogInformation("Restock completed Products={Rows} Logs={Logs}", result.TotalRowsAffected, restockMap.Count);
            db.ChangeTracker.Clear();
            var restocked = await db.Products.AsNoTracking().Where(p => skus.Contains(p.Sku)).ToListAsync();
            foreach (var p in restocked) logger.LogInformation("{Sku} Stock={Stock} Active={Active}", p.Sku, p.StockQuantity, p.IsActive);
        }
        catch (Exception ex) { await tx.RollbackAsync(); logger.LogError(ex, "Restock failed"); }
        db.ChangeTracker.Clear();
    }

    private static async Task Example2_OrderFulfillment(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 2: Order Fulfillment Workflow");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var product1 = new Product { Sku = $"SHIP1-{suffix}", Name = "Widget A", Price = 29.99m, StockQuantity = 100, IsActive = true, Category = "Widgets", LastRestocked = DateTime.UtcNow };
        var product2 = new Product { Sku = $"SHIP2-{suffix}", Name = "Widget B", Price = 39.99m, StockQuantity = 50, IsActive = true, Category = "Widgets", LastRestocked = DateTime.UtcNow };
        var customer = new Customer { Email = $"fulfillment.{suffix}@example.com", Name = "Fulfillment Customer", IsActive = true, LoyaltyPoints = 100, CreatedAt = DateTime.UtcNow };
        db.Products.AddRange(product1, product2);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var orders = new[]
        {
            new Order { OrderNumber = $"SHIP-ORD1-{suffix}", CustomerId = customer.Id, OrderDate = DateTime.UtcNow.AddDays(-2), Status = OrderStatus.Pending, TotalAmount = 29.99m },
            new Order { OrderNumber = $"SHIP-ORD2-{suffix}", CustomerId = customer.Id, OrderDate = DateTime.UtcNow.AddDays(-1), Status = OrderStatus.Pending, TotalAmount = 39.99m }
        };
        db.Orders.AddRange(orders);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created {Count} pending orders suffix={Suffix}", orders.Length, suffix);

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var shippedDate = DateTime.UtcNow;
            var orderNumbers = orders.Select(o => o.OrderNumber).ToList();
            var result = await db.BulkExecuteAsync(batch =>
            {
                foreach (var n in orderNumbers) batch.Update<Order>(op => op.Where(o => o.OrderNumber == n && o.Status == OrderStatus.Pending).Set(o => o.Status, OrderStatus.Shipped).Set(o => o.ShippedDate, shippedDate));
                batch.Update<Customer>(op => op.Where(c => c.Id == customer.Id).Set(c => c.LoyaltyPoints, c => c.LoyaltyPoints + 20).Set(c => c.LastOrderDate, shippedDate));
                batch.Update<Product>(op => op.Where(p => p.Sku == product1.Sku).Set(p => p.StockQuantity, p => p.StockQuantity - 1));
                batch.Update<Product>(op => op.Where(p => p.Sku == product2.Sku).Set(p => p.StockQuantity, p => p.StockQuantity - 1));
            });
            await tx.CommitAsync();
            logger.LogInformation("Fulfillment ops={Count} rows={Rows}", result.Operations.Count, result.TotalRowsAffected);
            db.ChangeTracker.Clear();
            var shipped = await db.Orders.CountAsync(o => orderNumbers.Contains(o.OrderNumber) && o.Status == OrderStatus.Shipped);
            var upd = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customer.Id);
            logger.LogInformation("Shipped={Shipped} Points={Points}", shipped, upd.LoyaltyPoints);
        }
        catch (Exception ex) { await tx.RollbackAsync(); logger.LogError(ex, "Fulfillment failed"); }
        db.ChangeTracker.Clear();
    }

    private static async Task Example3_PriceUpdate(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 3: Dynamic Price Update Category-Based");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var catApparel = $"Apparel-{suffix}";
        var catElec = $"Electronics-{suffix}";
        var catAcc = $"Accessories-{suffix}";
        var products = new[]
        {
            new Product { Sku = $"PRICE1-{suffix}", Name = "Summer Shirt", Price = 49.99m, StockQuantity = 100, IsActive = true, Category = catApparel, LastRestocked = DateTime.UtcNow },
            new Product { Sku = $"PRICE2-{suffix}", Name = "Winter Jacket", Price = 129.99m, StockQuantity = 50, IsActive = true, Category = catApparel, LastRestocked = DateTime.UtcNow },
            new Product { Sku = $"PRICE3-{suffix}", Name = "Smart Watch", Price = 299.99m, StockQuantity = 30, IsActive = true, Category = catElec, LastRestocked = DateTime.UtcNow },
            new Product { Sku = $"PRICE4-{suffix}", Name = "Laptop Stand", Price = 39.99m, StockQuantity = 75, IsActive = true, Category = catAcc, LastRestocked = DateTime.UtcNow }
        };
        db.Products.AddRange(products);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created {Count} products suffix={Suffix}", products.Length, suffix);

        var result = await db.BulkExecuteAsync(batch =>
        {
            batch.Update<Product>(op => op.Where(p => p.Category == catApparel && p.IsActive).Set(p => p.Price, p => p.Price * 0.8m));
            batch.Update<Product>(op => op.Where(p => p.Category == catElec && p.IsActive).Set(p => p.Price, p => p.Price * 0.9m));
            batch.Update<Product>(op => op.Where(p => p.Category == catAcc && p.IsActive).Set(p => p.Price, p => p.Price * 0.95m));
        });
        logger.LogInformation("Price updates ops={Ops} rows={Rows}", result.Operations.Count, result.TotalRowsAffected);
        db.ChangeTracker.Clear();
        var skus = products.Select(p => p.Sku).ToArray();
        var updated = await db.Products.AsNoTracking().Where(p => skus.Contains(p.Sku)).OrderBy(p => p.Category).ToListAsync();
        foreach (var g in updated.GroupBy(p => p.Category)) { logger.LogInformation("Category {Cat}", g.Key); foreach (var p in g) logger.LogInformation(" {Name}: {Price}", p.Name, p.Price); }
    }

    private static async Task Example4_CustomerLoyaltyBatch(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 4: Customer Loyalty Batch Processing");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var customers = new[]
        {
            new Customer { Email = $"bronze.{suffix}@example.com", Name = "Bronze Customer", IsActive = true, LoyaltyPoints = 150, CreatedAt = DateTime.UtcNow.AddMonths(-3) },
            new Customer { Email = $"silver.{suffix}@example.com", Name = "Silver Customer", IsActive = true, LoyaltyPoints = 650, CreatedAt = DateTime.UtcNow.AddMonths(-6) },
            new Customer { Email = $"gold.{suffix}@example.com", Name = "Gold Customer", IsActive = true, LoyaltyPoints = 1500, CreatedAt = DateTime.UtcNow.AddYears(-1) },
            new Customer { Email = $"inactive.{suffix}@example.com", Name = "Inactive Customer", IsActive = true, LoyaltyPoints = 50, CreatedAt = DateTime.UtcNow.AddYears(-2), LastOrderDate = DateTime.UtcNow.AddYears(-1) }
        };
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        logger.LogInformation("Created {Count} customers suffix={Suffix}", customers.Length, suffix);
        var sixMonthsAgo = DateTime.UtcNow.AddMonths(-6);
        var result = await db.BulkExecuteAsync(batch =>
        {
            batch.Update<Customer>(op => op.Where(c => c.Email.Contains(suffix) && c.LoyaltyPoints >= 100 && c.LoyaltyPoints < 500 && c.IsActive).Set(c => c.LoyaltyPoints, c => c.LoyaltyPoints + 50));
            batch.Update<Customer>(op => op.Where(c => c.Email.Contains(suffix) && c.LoyaltyPoints >= 500 && c.LoyaltyPoints < 1000 && c.IsActive).Set(c => c.LoyaltyPoints, c => c.LoyaltyPoints + 100));
            batch.Update<Customer>(op => op.Where(c => c.Email.Contains(suffix) && c.LoyaltyPoints >= 1000 && c.IsActive).Set(c => c.LoyaltyPoints, c => c.LoyaltyPoints + 200));
            batch.Update<Customer>(op => op.Where(c => c.Email.Contains(suffix) && c.LastOrderDate != null && c.LastOrderDate < sixMonthsAgo && c.IsActive).Set(c => c.IsActive, false));
        });
        logger.LogInformation("Loyalty batch ops={Ops} rows={Rows}", result.Operations.Count, result.TotalRowsAffected);
        db.ChangeTracker.Clear();
        var updated = await db.Customers.AsNoTracking().Where(c => c.Email.Contains(suffix)).OrderBy(c => c.Email).ToListAsync();
        foreach (var c in updated) { var tier = c.LoyaltyPoints >= 1000 ? "Gold" : c.LoyaltyPoints >= 500 ? "Silver" : c.LoyaltyPoints >= 100 ? "Bronze" : "New"; logger.LogInformation("{Name}: {Tier} {Points} Active={Active}", c.Name, tier, c.LoyaltyPoints, c.IsActive); }
    }
}
