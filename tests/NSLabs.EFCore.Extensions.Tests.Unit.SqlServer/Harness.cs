using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

internal static class Harness
{
    public static (string Sql, IReadOnlyList<SqlParam> Params) GenerateSingle(
        Action<IBulkBatch> build,
        BulkExecuteOptions? options = null,
        int? efCompatibilityLevel = null)
    {
        var chunks = Generate(build, options, efCompatibilityLevel);
        Assert.True(chunks.Count == 1, $"Expected exactly 1 chunk but got {chunks.Count}.");
        return (Normalize(chunks[0].CommandText), chunks[0].Parameters);
    }

    public static IReadOnlyList<SqlChunkPlan> Generate(
        Action<IBulkBatch> build,
        BulkExecuteOptions? options = null,
        int? efCompatibilityLevel = null)
    {
        var builder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(
                "Server=tcp:localhost,1433;Database=BulkExtensionsTest;User Id=test;Password=test;TrustServerCertificate=True;",
                b =>
                {
                    if (efCompatibilityLevel.HasValue)
                    {
                        b.UseCompatibilityLevel(efCompatibilityLevel.Value);
                    }
                });
        using var context = new SqlServerUnitTestDbContext(builder.Options);
        var batch = new BulkBatch(context);
        build(batch);

        // Generate through the real provider path (EF compat inheritance),
        // exactly as BulkBatch.ExecuteCoreAsync does in production.
        var provider = new SqlServerProvider();
        return provider.Generate(
            batch.Operations,
            options?.MaxParametersPerCommand ?? BulkExecuteOptionsDefaults.MaxParametersPerCommand,
            context);
    }

    public static string Normalize(string sql)
        => Regex.Replace(sql, @"\s+", " ").Trim();

    public static Dictionary<string, object?> Params(IReadOnlyList<SqlParam> parameters)
        => parameters.ToDictionary(p => p.Name, p => p.Value);

    internal static class BulkExecuteOptionsDefaults
    {
        public const int MaxParametersPerCommand = 2000;
    }

    /// <summary>
    /// SqlServer unit-test model: adds the store-generated CreatedAt column
    /// that the provider-neutral <see cref="TestDbContext"/> intentionally omits.
    /// </summary>
    internal sealed class SqlServerUnitTestDbContext(DbContextOptions<TestDbContext> options) : TestDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Item>().Property(x => x.CreatedAt).HasComputedColumnSql("GETDATE()");
        }
    }
}
