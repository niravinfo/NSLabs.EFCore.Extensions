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
    Console.Error.WriteLine("  Windows (LocalDB): Server=(localdb)\\mssqllocaldb;Database=NSLabsBulkExtensionsSample;Trusted_Connection=True;TrustServerCertificate=True");
    Console.Error.WriteLine("  Linux/Docker:      Server=localhost,1433;Database=NSLabsBulkExtensionsSample;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=True");
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
    // Docker healthcheck + depends_on: service_healthy already waits for SQL Server to be ready.
    // No retry loop here - fail fast if unreachable (Option A: rely on Docker advantage).
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
    try
    {
        // EnsureCreated is idempotent: creates DB + schema if not exists, no-op otherwise.
        await db.Database.EnsureCreatedAsync();
        logger.LogInformation("Database ready");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed. Ensure SQL Server is reachable. Windows: sqllocaldb start mssqllocaldb | Linux/Docker: docker compose -f samples/NSLabs.EFCore.Extensions.Samples.SqlServer/docker-compose.yml up -d (or docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='YourStrong@Passw0rd' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest) and set ConnectionStrings__DefaultConnection if needed.");
        throw;
    }
}
