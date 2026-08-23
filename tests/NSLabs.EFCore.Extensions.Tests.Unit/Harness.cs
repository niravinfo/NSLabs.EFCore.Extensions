using System.Text.RegularExpressions;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit;

internal static class Harness
{
    public static (string Sql, IReadOnlyList<SqlParam> Params) GenerateSingle(
        Action<IBulkBatch> build,
        BulkExecuteOptions? options = null)
    {
        var chunks = Generate(build, options);
        Assert.True(chunks.Count == 1, $"Expected exactly 1 chunk but got {chunks.Count}.");
        return (Normalize(chunks[0].CommandText), chunks[0].Parameters);
    }

    public static IReadOnlyList<SqlChunkPlan> Generate(Action<IBulkBatch> build, BulkExecuteOptions? options = null)
    {
        using var context = new TestDbContext();
        var batch = new BulkBatch(context);
        build(batch);
        return SqlServerSqlGenerator.Generate(
            batch.Operations,
            options?.MaxParametersPerCommand ?? BulkExecuteOptionsDefaults.MaxParametersPerCommand);
    }

    public static string Normalize(string sql)
        => Regex.Replace(sql, @"\s+", " ").Trim();

    public static Dictionary<string, object?> Params(IReadOnlyList<SqlParam> parameters)
        => parameters.ToDictionary(p => p.Name, p => p.Value);

    internal static class BulkExecuteOptionsDefaults
    {
        public const int MaxParametersPerCommand = 2000;
    }
}
