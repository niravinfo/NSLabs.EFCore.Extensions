using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSLabs.EFCore.Extensions.Samples.Data;
using NSLabs.EFCore.Extensions.Samples.Scenarios;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Connection string 'DefaultConnection' not found. Check appsettings.json or SAMPLE_CONNECTION_STRING env var.");
    return 1;
}

builder.Services.AddDbContext<SampleDbContext>(options =>
{
    options.UseSqlServer(connectionString);
});

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();

logger.LogInformation("NSLabs.EFCore.Extensions - Samples.SqlServer");
logger.LogInformation("Connection: {Conn}", connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(s => s.Contains("Server", StringComparison.OrdinalIgnoreCase)) ?? "configured");

await EnsureDatabaseAsync(scopeFactory, logger);

while (true)
{
    PrintMenu(logger);
    var choice = Console.ReadLine()?.Trim();
    if (choice == "0" || string.IsNullOrEmpty(choice))
    {
        logger.LogInformation("Exiting samples");
        break;
    }

    try
    {
        await ExecuteScenarioAsync(scopeFactory, choice, logger);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Scenario {Choice} failed", choice);
    }

    if (!Console.IsInputRedirected)
    {
        logger.LogInformation("Press any key to continue...");
        try { Console.ReadKey(intercept: true); } catch { await Task.Delay(100); }
        try { Console.Clear(); } catch { }
    }
    else
    {
        // Non-interactive (piped) run - small delay to avoid tight loop
        await Task.Delay(200);
    }
}

return 0;

static void PrintMenu(ILogger logger)
{
    logger.LogInformation("======================== MAIN MENU ========================");
    logger.LogInformation(" BASIC:      1 - Basic examples");
    logger.LogInformation(" ADVANCED:   2 - Advanced examples");
    logger.LogInformation(" TX:         3 - Transaction examples");
    logger.LogInformation(" REALWORLD:  4 - Real-world examples");
    logger.LogInformation(" TABLE API:  6 - Table API and Options (CreateBulkBatch, BulkUpdateAsync, ThrowIfZeroAffected, chunking)");
    logger.LogInformation(" OTHER:      5 - Run ALL  |  7 - Clean database  |  0 - Exit");
    logger.LogInformation("===========================================================");
}

static async Task ExecuteScenarioAsync(IServiceScopeFactory scopeFactory, string choice, ILogger logger)
{
    // Each scenario gets an isolated DbContext to avoid ChangeTracker staleness
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();

    switch (choice)
    {
        case "1": await BasicExamples.RunAllAsync(db, logger); break;
        case "2": await AdvancedExamples.RunAllAsync(db, logger); break;
        case "3": await TransactionExamples.RunAllAsync(db, logger); break;
        case "4": await RealWorldExamples.RunAllAsync(db, logger); break;
        case "5":
            await BasicExamples.RunAllAsync(db, logger);
            db.ChangeTracker.Clear();
            await AdvancedExamples.RunAllAsync(db, logger);
            db.ChangeTracker.Clear();
            await TransactionExamples.RunAllAsync(db, logger);
            db.ChangeTracker.Clear();
            await RealWorldExamples.RunAllAsync(db, logger);
            db.ChangeTracker.Clear();
            await TableApiAndOptionsExamples.RunAllAsync(db, logger);
            logger.LogInformation("All examples completed");
            break;
        case "6": await TableApiAndOptionsExamples.RunAllAsync(db, logger); break;
        case "7": await CleanDatabaseAsync(db, logger); break;
        default: logger.LogWarning("Invalid choice {Choice}", choice); break;
    }
}

static async Task EnsureDatabaseAsync(IServiceScopeFactory scopeFactory, ILogger logger)
{
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        if (!canConnect)
        {
            logger.LogInformation("Database not found, creating...");
            await db.Database.EnsureCreatedAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
        }
        
        logger.LogInformation("Database ready");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed. Ensure SQL Server is reachable. For LocalDB: sqllocaldb start mssqllocaldb or docker run -e ACCEPT_EULA=Y -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest");
        throw;
    }
}

static async Task CleanDatabaseAsync(SampleDbContext db, ILogger logger)
{
    logger.LogInformation("Clean database requested");
    Console.Write("Are you sure? Type 'yes' to confirm: ");
    var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
    if (confirm != "yes")
    {
        logger.LogInformation("Cancelled");
        return;
    }

    await DatabaseHelper.ClearAllAsync(db, logger);
    logger.LogInformation("Database cleaned");
}
