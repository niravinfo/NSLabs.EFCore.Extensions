using Microsoft.EntityFrameworkCore;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Benchmarks.Infrastructure;

/// <summary>
/// Offline SQL Server generation harness: same fake-connection pattern as the
/// golden-SQL unit tests — builds the EF model and runs the real provider
/// Generate path with no database and no network I/O.
/// </summary>
internal static class SqlServerGenerationHarness
{
    internal const int DefaultMaxParametersPerCommand = 2000;

    public static BenchmarkDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseSqlServer(
                "Server=tcp:localhost,1433;Database=BulkBenchmarks;User Id=test;Password=test;TrustServerCertificate=True;")
            .Options;
        return new BenchmarkDbContext(options);
    }

    public static IReadOnlyList<SqlChunkPlan> Generate(
        BulkBatch batch,
        int maxParametersPerCommand = DefaultMaxParametersPerCommand,
        BenchmarkDbContext? context = null)
    {
        var ownsContext = context is null;
        context ??= CreateContext();
        try
        {
            var provider = new SqlServerProvider();
            return provider.Generate(batch.Operations, maxParametersPerCommand, context);
        }
        finally
        {
            if (ownsContext)
            {
                context.Dispose();
            }
        }
    }
}
