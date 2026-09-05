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
    Console.Error.WriteLine("  Local:  Host=localhost;Port=5432;Database=nsamples;Username=postgres;Password=postgres");
    Console.Error.WriteLine("  Docker: Host=postgres;Port=5432;Database=nsamples;Username=postgres;Password=postgres");
    return 1;
}

builder.Services.AddDbContext<SampleDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();

logger.LogInformation("NSLabs.EFCore.Extensions - Samples.Npgsql");
logger.LogInformation("Connection: {Conn}", connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(s => s.Contains("Host", StringComparison.OrdinalIgnoreCase)) ?? "configured");

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
    // Docker healthcheck + depends_on: service_healthy already waits for PostgreSQL to be ready.
    // No retry loop here - fail fast if unreachable.
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
    try
    {
        // Fresh database on every run: drop if it exists, then recreate the full
        // schema from the current model. Rerunnable with zero setup, and new
        // entities always get their tables — no manual steps, no raw SQL.
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        logger.LogInformation("Database ready");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed. Ensure PostgreSQL is reachable. Docker: docker compose -f samples/NSLabs.EFCore.Extensions.Samples.Npgsql/docker-compose.yml up -d (or docker run -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:17) and set ConnectionStrings__DefaultConnection if needed.");
        throw;
    }
}
