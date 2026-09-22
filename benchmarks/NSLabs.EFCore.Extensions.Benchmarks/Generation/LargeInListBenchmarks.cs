using BenchmarkDotNet.Attributes;
using NSLabs.EFCore.Extensions.Benchmarks.Infrastructure;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Benchmarks.Generation;

/// <summary>
/// Large-IN list bind + generate cost (500/5,000/20,000 all sit above the
/// provider large-list thresholds — SqlServer 100, Sqlite 50 — so the JSON /
/// ANY fast path is exercised). Offline — no database.
/// </summary>
public class LargeInListBenchmarks : BenchmarkBase
{
    private BenchmarkDbContext _context = null!;
    private int[] _ids = null!;
    private int[] _warmIds = null!;

    [Params(500, 5_000, 20_000)]
    public int ListSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = SqlServerGenerationHarness.CreateContext();
        _ids = new int[ListSize];
        for (var i = 0; i < ListSize; i++)
        {
            _ids[i] = i + 1;
        }

        _warmIds = _ids;
        var warm = new BulkBatch(_context);
        warm.Update<BenchItem>(op => op
            .Where(x => _warmIds.Contains(x.Id))
            .Set(x => x.Active, true));
        SqlServerGenerationHarness.Generate(warm, context: _context);
    }

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    [Benchmark]
    public int BindAndGenerateLargeIn()
    {
        var ids = _ids;
        var batch = new BulkBatch(_context);
        batch.Update<BenchItem>(op => op
            .Where(x => ids.Contains(x.Id))
            .Set(x => x.Active, false)
            .Set(x => x.Key3, 42));
        return SqlServerGenerationHarness.Generate(batch, context: _context).Count;
    }
}
