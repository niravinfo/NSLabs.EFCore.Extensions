using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Sqlite;

public class SqliteBulkExecuteOptionsTests : SqliteTestBase
{
    public SqliteBulkExecuteOptionsTests(SqliteFixture fixture) : base(fixture) { }

    private SqliteTestDbContext CreateConfiguredContext(Action<BulkExecuteOptions> configure)
    {
        var opts = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(Fixture.ConnectionString)
            .UseBulkExecute(configure)
            .Options;

        return new SqliteTestDbContext(opts);
    }

    private async Task SeedItemsAsync(params (int Id, string Key1)[] rows)
    {
        await using var ctx = Fixture.CreateContext();
        await ctx.Items.ExecuteDeleteAsync();
        foreach (var (id, key1) in rows)
        {
            ctx.Items.Add(new Item { Id = id, Key1 = key1, Key2 = 0, Key3 = 0 });
        }

        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task DI_ThrowIfZeroAffected_applies_without_per_call_options()
    {
        await SeedItemsAsync((9601, "orig"));
        await using var db = CreateConfiguredContext(o => o.ThrowIfZeroAffected = true);

        var ex = await Assert.ThrowsAsync<BulkZeroRowsAffectedException>(() =>
            db.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == 9601).Set(x => x.Key1, "hit"));
                b.Update<Item>(op => op.Where(x => x.Id == -9601).Set(x => x.Key1, "miss"));
            }));

        Assert.Equal(1, ex.OperationIndex);
        Assert.Equal("Item", ex.EntityType);
    }

    [Fact]
    public async Task Explicit_object_suppresses_DI_ThrowIfZeroAffected_for_that_call()
    {
        await SeedItemsAsync((9602, "orig"));
        await using var db = CreateConfiguredContext(o => o.ThrowIfZeroAffected = true);

        var result = await db.BulkExecuteAsync(
            b => b.Update<Item>(op => op.Where(x => x.Id == -9602).Set(x => x.Key1, "miss")),
            new BulkExecuteOptions());

        Assert.Equal(0, result.TotalRowsAffected);
    }

    [Fact]
    public async Task No_UseBulkExecute_means_zero_match_does_not_throw()
    {
        await SeedItemsAsync((9603, "orig"));
        await using var db = Fixture.CreateContext();

        var result = await db.BulkExecuteAsync(b =>
            b.Update<Item>(op => op.Where(x => x.Id == -9603).Set(x => x.Key1, "miss")));

        Assert.Equal(0, result.TotalRowsAffected);
    }

    [Fact]
    public async Task DI_MaxParametersPerCommand_chunks_end_to_end()
    {
        var ids = Enumerable.Range(9610, 5).ToArray();
        await SeedItemsAsync(ids.Select(id => (id, "orig")).ToArray());
        await using var db = CreateConfiguredContext(o => o.MaxParametersPerCommand = 4);

        var result = await db.BulkExecuteAsync(b =>
        {
            foreach (var id in ids)
            {
                var cid = id;
                b.Update<Item>(op => op.Where(x => x.Id == cid).Set(x => x.Key1, "upd-" + cid));
            }
        });

        Assert.Equal(ids.Length, result.Operations.Count);
        Assert.Equal(ids.Length, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        var items = await verify.Items
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        foreach (var id in ids)
        {
            Assert.Equal("upd-" + id, items[id].Key1);
        }
    }

    [Fact]
    public async Task Deferred_batch_uses_DI_snapshot_at_ExecuteAsync_time()
    {
        await SeedItemsAsync((9620, "orig"));
        await using var db = CreateConfiguredContext(o => o.ThrowIfZeroAffected = true);

        IBulkBatch batch = db.CreateBulkBatch();
        batch.Update<Item>(op => op.Where(x => x.Id == -9620).Set(x => x.Key1, "miss"));

        var ex = await Assert.ThrowsAsync<BulkZeroRowsAffectedException>(() => batch.ExecuteAsync());
        Assert.Equal(0, ex.OperationIndex);
    }
}
