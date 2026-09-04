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
    Console.Error.WriteLine("Connection string 'DefaultConnection' not found. Check appsettings.json or set ConnectionStrings__DefaultConnection env var.");
    Console.Error.WriteLine("  Default (file): Data Source=nsamples.db");
    return 1;
}

builder.Services.AddDbContext<SampleDbContext>(options =>
{
    options.UseSqlite(connectionString);
});

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();

logger.LogInformation("NSLabs.EFCore.Extensions - Samples.Sqlite");
logger.LogInformation("Connection: {Conn}", connectionString);

await EnsureDatabaseAsync(scopeFactory, logger);

try
{
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();

    await BasicExamples.RunAllAsync(db, logger);
    db.ChangeTracker.Clear();

    await AdvancedExamples.RunAllAsync(db, logger);
    db.ChangeTracker.Clear();

    await TransactionExamples.RunAllAsync(db, logger);
    db.ChangeTracker.Clear();

    await RealWorldExamples.RunAllAsync(db, logger);
    db.ChangeTracker.Clear();

    await TableApiAndOptionsExamples.RunAllAsync(db, logger);

    logger.LogInformation("All examples completed successfully");
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Scenario execution failed");
    return 1;
}

static async Task EnsureDatabaseAsync(IServiceScopeFactory scopeFactory, ILogger logger)
{
    // SQLite file DB: zero-config, auto-created on first run. No Docker / server needed.
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
    try
    {
        // EnsureCreated is idempotent: creates file + schema if not exists, no-op otherwise.
        await db.Database.EnsureCreatedAsync();
        logger.LogInformation("Database ready");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed. For SQLite file DB ensure the app can write to the current directory, or set ConnectionStrings__DefaultConnection (e.g. Data Source=/tmp/nsamples.db).");
        throw;
    }
}
