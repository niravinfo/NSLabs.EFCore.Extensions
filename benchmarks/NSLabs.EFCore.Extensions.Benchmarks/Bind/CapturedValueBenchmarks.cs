using BenchmarkDotNet.Attributes;
using NSLabs.EFCore.Extensions.Benchmarks.Infrastructure;

namespace NSLabs.EFCore.Extensions.Benchmarks.Bind;

/// <summary>
/// Captured-value shapes actually written by callers (README, samples and
/// golden-SQL tests), rather than synthetic benchmark-only expressions.
/// Offline - no database.
/// </summary>
public class CapturedValueBenchmarks : BenchmarkBase
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
    public int BindCapturedConstant()
    {
        var batch = new BulkBatch(_context);
        for (var i = 0; i < Operations; i++)
        {
            var captured = i;
            batch.Update<BenchItem>(op => op
                .Where(x => x.Id == captured)
                .Set(x => x.Key2, captured));
        }

        return batch.Operations.Count;
    }

    [Benchmark]
    public int BindCapturedArithmetic()
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

    [Benchmark]
    public int BindCapturedStringConcat()
    {
        var batch = new BulkBatch(_context);
        for (var i = 0; i < Operations; i++)
        {
            var id = i.ToString();
            batch.Update<BenchItem>(op => op
                .Where(x => x.Id == i)
                .Set(x => x.Key1, "v" + id));
        }

        return batch.Operations.Count;
    }

    [Benchmark]
    public int BindInterpolatedString()
    {
        var batch = new BulkBatch(_context);
        for (var i = 0; i < Operations; i++)
        {
            batch.Update<BenchItem>(op => op
                .Where(x => x.Id == i)
                .Set(x => x.Key1, $"V{i}"));
        }

        return batch.Operations.Count;
    }

    [Benchmark]
    public int BindCapturedMethodArgument()
    {
        var batch = new BulkBatch(_context);
        for (var i = 0; i < Operations; i++)
        {
            var replacement = i.ToString();
            batch.Update<BenchItem>(op => op
                .Where(x => x.Id == i)
                .Set(x => x.Key1, x => x.Key1.Replace("a", replacement)));
        }

        return batch.Operations.Count;
    }

    [Benchmark]
    public int BindCapturedDecimalFactor()
    {
        var batch = new BulkBatch(_context);
        for (var i = 0; i < Operations; i++)
        {
            var factor = 1.1m + i;
            batch.Update<BenchOrder>(op => op
                .Where(x => x.OrderNo == "O-1")
                .Set(x => x.Amount, x => x.Amount * factor));
        }

        return batch.Operations.Count;
    }
}
