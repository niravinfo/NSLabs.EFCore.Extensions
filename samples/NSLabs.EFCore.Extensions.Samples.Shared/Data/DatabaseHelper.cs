using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace NSLabs.EFCore.Extensions.Samples.Data;

public static class DatabaseHelper
{
    /// <summary>
    /// Single source of truth for database cleanup. Used both for isolated scenario runs
    /// and for the manual "Clean database" menu. FK-safe order, identity reseed best-effort.
    /// </summary>
    public static async Task ClearAllAsync(SampleDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Clearing database");
        
        try
        {
            // FK-safe order: children first
            await db.Database.ExecuteSqlRawAsync("DELETE FROM [OrderItems]", cancellationToken);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM [Orders]", cancellationToken);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM [InventoryLogs]", cancellationToken);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM [Customers]", cancellationToken);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM [Products]", cancellationToken);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM [DailyArticleViews]", cancellationToken);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM [EnergyReadings]", cancellationToken);

            // Identity reseed (SQL Server only; skipped on SQLite/other providers to avoid log noise)
            if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                try { await db.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('[Products]', RESEED, 0)", cancellationToken); } catch { }
                try { await db.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('[Customers]', RESEED, 0)", cancellationToken); } catch { }
                try { await db.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('[Orders]', RESEED, 0)", cancellationToken); } catch { }
                try { await db.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('[OrderItems]', RESEED, 0)", cancellationToken); } catch { }
                try { await db.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('[InventoryLogs]', RESEED, 0)", cancellationToken); } catch { }
            }

            db.ChangeTracker.Clear();
            logger.LogDebug("Database cleared");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Database clear failed - continuing with existing data");
            db.ChangeTracker.Clear();
        }
    }
}
