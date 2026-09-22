using BenchmarkDotNet.Attributes;
using NSLabs.EFCore.Extensions.Benchmarks.Infrastructure;

namespace NSLabs.EFCore.Extensions.Benchmarks.Bind;

/// <summary>
/// Predicate translation cost during batch bind (P2 target):
/// comparison + LIKE-style methods + captured-closure compile fallback.
/// Offline — no database.
/// </summary>
public class PredicateTranslationBenchmarks : BenchmarkBase
{
    private BenchmarkDbContext _context = null!;

    [Params(10, 1_000, 10_000)]
    public int Operations { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = SqlServerGenerationHarness.CreateContext();
        // Warm the EF model so model-build cost is outside the measurement.
        _ = new BulkBatch(_context).Update<BenchItem>(op =>
            op.Where(x => x.Id == 0).Set(x => x.Key2, 0));
    }

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    [Benchmark]
    public int BindComparisonPredicates()
    {
        var batch = new BulkBatch(_context);
        for (var i = 0; i < Operations; i++)
        {
            var id = i;
            batch.Update<BenchItem>(op => op
                .Where(x => x.Id == id && x.Key2 >= id && x.Active)
                .Set(x => x.Key3, id));
        }

        return batch.Operations.Count;
    }

    [Benchmark]
    public int BindStartsWithPredicates()
    {
        var batch = new BulkBatch(_context);
        for (var i = 0; i < Operations; i++)
        {
            var prefix = $"key-{i}";
            batch.Update<BenchItem>(op => op
                .Where(x => x.Key1.StartsWith(prefix))
                .Set(x => x.Active, true));
        }

        return batch.Operations.Count;
    }

    // Captured-closure arithmetic in the predicate forces LinqPredicateTranslator.Evaluate
    // down the Compile().DynamicInvoke() fallback (P2 concern C2).
    [Benchmark]
    public int BindClosureCompileFallback()
    {
        var batch = new BulkBatch(_context);
        for (var i = 0; i < Operations; i++)
        {
            var threshold = i;
            var bump = 1;
            batch.Update<BenchItem>(op => op
                .Where(x => x.Id > threshold + bump)
                .Set(x => x.Key2, threshold));
        }

        return batch.Operations.Count;
    }
}
