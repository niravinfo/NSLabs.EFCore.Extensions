using BenchmarkDotNet.Attributes;
using NSLabs.EFCore.Extensions.Benchmarks.Infrastructure;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Benchmarks.Generation;

/// <summary>
/// Entity-row update bind + generate cost (P3 target — per-row expression rewrite
/// on the custom-match path; PK-match path is the cheap contrast).
/// Offline — no database.
/// </summary>
public class EntityRowMatchBenchmarks : BenchmarkBase
{
    private BenchmarkDbContext _context = null!;
    private List<BenchItem> _rows = null!;

    [Params(10, 1_000, 10_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = SqlServerGenerationHarness.CreateContext();
        _rows = SqliteDatabaseFactory.CreateItems(Rows);
        // Warm model + both bind paths.
        WarmUp();
    }

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    [Benchmark]
    public int BindAndGeneratePrimaryKeyMatch()
    {
        var batch = new BulkBatch(_context);
        batch.Update(_rows);
        return SqlServerGenerationHarness.Generate(batch, context: _context).Count;
    }

    [Benchmark]
    public int BindAndGenerateCustomMatch()
    {
        var batch = new BulkBatch(_context);
        batch.Update(_rows, (row, x) => x.Id == row.Id && x.Key1 == row.Key1);
        return SqlServerGenerationHarness.Generate(batch, context: _context).Count;
    }

    private void WarmUp()
    {
        var pk = new BulkBatch(_context);
        pk.Update(_rows);
        SqlServerGenerationHarness.Generate(pk, context: _context);

        var match = new BulkBatch(_context);
        match.Update(_rows, (row, x) => x.Id == row.Id && x.Key1 == row.Key1);
        SqlServerGenerationHarness.Generate(match, context: _context);
    }
}
