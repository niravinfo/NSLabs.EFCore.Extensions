using BenchmarkDotNet.Attributes;
using NSLabs.EFCore.Extensions.Benchmarks.Infrastructure;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Benchmarks.Generation;

/// <summary>
/// SQL chunk-plan generation cost (P4 target — intermediate-string emission).
/// Offline: real SqlServerProvider.Generate with a fake connection string.
/// </summary>
public class SqlGenerationBenchmarks : BenchmarkBase
{
    private BenchmarkDbContext _context = null!;

    [Params(10, 1_000, 10_000)]
    public int Operations { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = SqlServerGenerationHarness.CreateContext();
        // Warm provider + model outside the measurement.
        SqlServerGenerationHarness.Generate(BuildSampleBatch(_context, 1), context: _context);
    }

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    [Benchmark]
    public int BindAndGenerateUpdateStatements()
    {
        var batch = BuildSampleBatch(_context, Operations);
        var chunks = SqlServerGenerationHarness.Generate(batch, context: _context);
        return chunks.Count;
    }

    private static BulkBatch BuildSampleBatch(BenchmarkDbContext context, int operationCount)
    {
        var batch = new BulkBatch(context);
        for (var i = 0; i < operationCount; i++)
        {
            var id = i;
            var prefix = $"key-{i}";
            batch.Update<BenchItem>(op => op
                .Where(x => x.Id == id && x.Key1.StartsWith(prefix))
                .Set(x => x.Key2, id)
                .Set(x => x.Key3, id * 2)
                .Set(x => x.Active, true));
        }

        return batch;
    }
}
