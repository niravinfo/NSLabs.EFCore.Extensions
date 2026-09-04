using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.Sqlite;

internal static class SqliteHarness
{
    public static (string Sql, IReadOnlyList<SqlParam> Params) GenerateSingle(
        Action<IBulkBatch> build,
        BulkExecuteOptions? options = null)
    {
        var chunks = Generate(build, options);
        Assert.True(chunks.Count == 1, $"Expected exactly 1 chunk but got {chunks.Count}. Chunks: {string.Join(" | ", chunks.Select(c => c.CommandText))}");
        return (Normalize(chunks[0].CommandText), chunks[0].Parameters);
    }

    public static IReadOnlyList<SqlChunkPlan> Generate(Action<IBulkBatch> build, BulkExecuteOptions? options = null)
    {
        using var context = CreateContext();
        var batch = new BulkBatch(context);
        build(batch);
        return SqliteSqlGenerator.Generate(
            batch.Operations,
            options?.MaxParametersPerCommand ?? SqliteSqlGenerator.MaxParametersPerCommand);
    }

    public static TestDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        return new TestDbContext(opts);
    }

    public static string Normalize(string sql)
        => Regex.Replace(sql, @"\s+", " ").Trim();

    public static Dictionary<string, object?> Params(IReadOnlyList<SqlParam> parameters)
        => parameters.ToDictionary(p => p.Name, p => p.Value);
}
