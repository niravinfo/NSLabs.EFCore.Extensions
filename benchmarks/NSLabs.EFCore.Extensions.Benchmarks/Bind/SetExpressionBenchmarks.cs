using BenchmarkDotNet.Attributes;
using NSLabs.EFCore.Extensions.Benchmarks.Infrastructure;

namespace NSLabs.EFCore.Extensions.Benchmarks.Bind;

/// <summary>
/// Computed SET translation cost during batch bind (P2 target):
/// captured-closure member + non-entity subexpression hit the uncached
/// SetExpressionTranslator.Evaluate Compile-per-call path (P2 concern C1).
/// Offline — no database.
/// </summary>
public class SetExpressionBenchmarks : BenchmarkBase
{
    private BenchmarkDbContext _context = null!;

    [Params(10, 1_000, 10_000)]
    public int Operations { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = SqlServerGenerationHarness.CreateContext();
        _ = new BulkBatch(_context).Update<BenchItem>(op =>
            op.Where(x => x.Id == 0).Set(x => x.Key2, 0));
    }

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    [Benchmark]
    public int BindComputedColumnArithmetic()
    {
        var batch = new BulkBatch(_context);
        for (var i = 0; i < Operations; i++)
        {
            batch.Update<BenchItem>(op => op
                .Where(x => x.Id == i)
                .Set(x => x.Key3, x => x.Key2 * 2));
        }

        return batch.Operations.Count;
    }

    // factor * 2 is a non-entity subexpression => SetExpressionTranslator.Evaluate compiles every bind.
    [Benchmark]
    public int BindComputedWithCapturedSubexpression()
    {
        var batch = new BulkBatch(_context);
        for (var i = 0; i < Operations; i++)
        {
            var factor = i + 1;
            batch.Update<BenchItem>(op => op
                .Where(x => x.Id == i)
                .Set(x => x.Key2, x => x.Key2 + (factor * 2)));
        }

        return batch.Operations.Count;
    }
}
