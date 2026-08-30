using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSLabs.EFCore.Extensions.Samples.Data;
using NSLabs.EFCore.Extensions.Samples.Models;

namespace NSLabs.EFCore.Extensions.Samples.Scenarios;

public static class TableApiAndOptionsExamples
{
    public static async Task RunAllAsync(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("=== TABLE API AND OPTIONS EXAMPLES ===");
        
        await DatabaseHelper.ClearAllAsync(db, logger);
        
        await Example1_TableApiAsync(db, logger);
        await Example2_DeferredBatchAsync(db, logger);
        await Example3_EntityRowsAsync(db, logger);
        await Example4_OptionsChunkingAndLoggingAsync(db, logger);
        await Example5_ThrowIfZeroAffectedAsync(db, logger);
    }

    private static async Task Example1_TableApiAsync(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 1: Table API BulkUpdateAsync / BulkUpsertAsync");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        db.Products.Add(new Product
        {
            Sku = $"TAPI-{suffix}",
            Name = "Table API Product",
            Price = 10m,
            StockQuantity = 5,
            IsActive = true,
            Category = "Test",
            LastRestocked = DateTime.UtcNow
        });
        
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var r1 = await db.Products.BulkUpdateAsync(b =>
        {
            b.Add(op => op.Where(p => p.Sku == $"TAPI-{suffix}").Set(p => p.Price, 12m));
        });

        logger.LogInformation("BulkUpdateAsync rows={Rows}", r1.TotalRowsAffected);

        var r2 = await db.Customers.BulkUpsertAsync(b =>
        {
            b.Add(op => op.On(c => c.Email).Values(new Customer { Email = $"tapi.{suffix}@example.com", Name = "TableApi", IsActive = true, LoyaltyPoints = 0, CreatedAt = DateTime.UtcNow }));
        });
        
        logger.LogInformation("BulkUpsertAsync rows={Rows}", r2.TotalRowsAffected);
        db.ChangeTracker.Clear();
    }

    private static async Task Example2_DeferredBatchAsync(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 2: Deferred CreateBulkBatch");
        
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var sku = $"DEFER-{suffix}";
        db.Products.Add(new Product
        {

            Sku = sku,
            Name = "Deferred",
            Price = 20m,
            StockQuantity = 10,
            IsActive = true,
            Category = "Test",
            LastRestocked = DateTime.UtcNow
        });
        
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        IBulkBatch batch = db.CreateBulkBatch();
        batch.Update<Product>(op => op.Where(p => p.Sku == sku).Set(p => p.Price, 25m));
        batch.Update<Product>(op => op.Where(p => p.Sku == sku).Set(p => p.StockQuantity, 99));

        var r = await batch.ExecuteAsync();
        logger.LogInformation("Deferred batch ops={Ops} rows={Rows}", r.Operations.Count, r.TotalRowsAffected);
        db.ChangeTracker.Clear();
    }

    private static async Task Example3_EntityRowsAsync(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 3: Entity-row style Update(IEnumerable)");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var p1 = new Product { Sku = $"EROW1-{suffix}", Name = "Row1", Price = 5m, StockQuantity = 1, IsActive = true, Category = "Test", LastRestocked = DateTime.UtcNow };
        var p2 = new Product { Sku = $"EROW2-{suffix}", Name = "Row2", Price = 6m, StockQuantity = 1, IsActive = true, Category = "Test", LastRestocked = DateTime.UtcNow };
        db.Products.AddRange(p1, p2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        p1.Price = 50m; p2.Price = 60m;
        var r = await db.BulkExecuteAsync(b => { b.Update(new[] { p1, p2 }); });
        logger.LogInformation("Entity rows update rows={Rows}", r.TotalRowsAffected);

        var p3 = new Product { Sku = $"EROW3-{suffix}", Name = "Row3", Price = 7m, StockQuantity = 1, IsActive = true, Category = "Test", LastRestocked = DateTime.UtcNow };
        db.Products.Add(p3);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        
        p3.Price = 70m;
        var r2 = await db.BulkExecuteAsync(b => { b.Update(new[] { p3 }, (row, x) => x.Sku == row.Sku); });
        logger.LogInformation("Custom match rows={Rows}", r2.TotalRowsAffected);
        db.ChangeTracker.Clear();
    }

    private static async Task Example4_OptionsChunkingAndLoggingAsync(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 4: Options MaxParametersPerCommand + OnCommandText + CommandTimeout");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 5; i++)
        {
            db.Products.Add(new Product
            {

                Sku = $"CHUNK-{suffix}-{i}",
                Name = $"Chunk {i}",
                Price = 10m + i,
                StockQuantity = 10,
                IsActive = true,
                Category = "Test",
                LastRestocked = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var logs = new List<string>();
        var result = await db.BulkExecuteAsync(b =>
        {
            for (var i = 0; i < 5; i++)
            {
                var sku = $"CHUNK-{suffix}-{i}";
                b.Update<Product>(op => op.Where(p => p.Sku == sku).Set(p => p.Price, 99m));
            }
        }, new BulkExecuteOptions
        {
            MaxParametersPerCommand = 4,
            CommandTimeout = 30,
            OnCommandText = sql =>
            {
                logs.Add(sql);
                logger.LogDebug("Generated SQL chunk length {Len}", sql.Length);
            }
        });
        
        logger.LogInformation("Chunked batch chunks={Chunks} rows={Rows}", logs.Count, result.TotalRowsAffected);

        foreach (var op in result.Operations)
        {
            logger.LogInformation("Op {Entity} {Rows}", op.EntityType, op.RowsAffected);
        }

        db.ChangeTracker.Clear();
    }

    private static async Task Example5_ThrowIfZeroAffectedAsync(SampleDbContext db, ILogger logger)
    {
        logger.LogInformation("Example 5: ThrowIfZeroAffected");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var sku = $"ZERO-{suffix}";
        db.Products.Add(new Product { Sku = sku, Name = "Zero", Price = 10m, StockQuantity = 1, IsActive = true, Category = "Test", LastRestocked = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        try
        {
            _ = await db.BulkExecuteAsync(b =>
            {
                b.Update<Product>(op => op.Where(p => p.Sku == sku).Set(p => p.Price, 11m));
                b.Update<Product>(op => op.Where(p => p.Sku == $"NOPE-{suffix}").Set(p => p.Price, 99m));
            }, new BulkExecuteOptions { ThrowIfZeroAffected = true });
        }
        catch (BulkZeroRowsAffectedException ex)
        {
            logger.LogWarning("ThrowIfZeroAffected caught OperationIndex={Idx} Entity={Entity}", ex.OperationIndex, ex.EntityType);
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            _ = await db.BulkExecuteAsync(b =>
            {
                b.Update<Product>(op => op.Where(p => p.Sku == sku).Set(p => p.Price, 12m));
                b.Update<Product>(op => op.Where(p => p.Sku == $"NOPE2-{suffix}").Set(p => p.Price, 99m));
            }, new BulkExecuteOptions { ThrowIfZeroAffected = true });
            await tx.CommitAsync();
        }
        catch (BulkZeroRowsAffectedException ex)
        {
            await tx.RollbackAsync();
            logger.LogInformation("Rolled back atomically due to zero-affected op {Idx}", ex.OperationIndex);
        }
        
        db.ChangeTracker.Clear();
    }
}
