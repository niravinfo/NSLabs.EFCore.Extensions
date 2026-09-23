using BenchmarkDotNet.Attributes;
using NSLabs.EFCore.Extensions.Benchmarks.Infrastructure;

namespace NSLabs.EFCore.Extensions.Benchmarks.Execution;

/// <summary>
/// End-to-end execute against shared-cache in-memory SQLite: isolates
/// command preparation / parameter binding / reader overhead with no network.
/// Real SqlServer/Npgsql runs are intentionally out of scope (noisy, Docker-dependent).
/// </summary>
public class SqliteExecuteBenchmarks : BenchmarkBase
{
    private const int SeedCount = 2_000;

    private SqliteDatabaseFactory _database = null!;
    private BenchmarkDbContext _context = null!;
    private int[] _ids = null!;

    [Params(10, 100, 1_000)]
    public int Operations { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _database = new SqliteDatabaseFactory(SeedCount);
        _context = _database.CreateContext();
        _ids = new int[Operations];
        for (var i = 0; i < Operations; i++)
        {
            _ids[i] = i + 1;
        }

        // Warm provider registration, model, and connection.
        ExecuteUpdateOnce().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _context.Dispose();
        _database.Dispose();
    }

    [Benchmark]
    public async Task<int> ExecuteUpdateWhere()
    {
        var result = await _context.BulkExecuteAsync(b =>
        {
            for (var i = 0; i < Operations; i++)
            {
                var id = _ids[i];
                b.Update<BenchItem>(op => op
                    .Where(x => x.Id == id)
                    .Set(x => x.Key2, id)
                    .Set(x => x.Active, true));
            }
        }).ConfigureAwait(false);

        return result.TotalRowsAffected;
    }

    [Benchmark]
    public async Task<int> ExecuteEntityRowPrimaryKeyMatch()
    {
        var rows = SqliteDatabaseFactory.CreateItems(Operations);
        // Ids must match seeded rows so the PK-match update affects rows.
        var result = await _context.BulkExecuteAsync(b => b.Update(rows)).ConfigureAwait(false);
        return result.TotalRowsAffected;
    }

    [Benchmark]
    public async Task<int> ExecuteLargeInList()
    {
        var ids = _ids;
        var result = await _context.BulkExecuteAsync(b =>
            b.Update<BenchItem>(op => op
                .Where(x => ids.Contains(x.Id))
                .Set(x => x.Key3, 7)
                .Set(x => x.Active, true))).ConfigureAwait(false);

        return result.TotalRowsAffected;
    }

    private Task<BulkExecuteResult> ExecuteUpdateOnce()
        => _context.BulkExecuteAsync(b =>
            b.Update<BenchItem>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, 1)));
}
