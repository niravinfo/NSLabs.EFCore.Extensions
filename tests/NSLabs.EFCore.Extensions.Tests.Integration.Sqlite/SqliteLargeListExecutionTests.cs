using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Sqlite;

public class SqliteLargeListExecutionTests : SqliteTestBase
{
    public SqliteLargeListExecutionTests(SqliteFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Update_with_5000_ids_matches_5000_rows()
    {
        const int baseId = 50000;
        var ids = Enumerable.Range(baseId, 6000).ToList();
        var targets = ids.Take(5000).ToList();

        await using (var context = Fixture.CreateContext())
        {
            await context.Items.Where(x => x.Id >= baseId && x.Id < baseId + 6000).ExecuteDeleteAsync();
            context.Items.AddRange(ids.Select(id => new Item { Id = id, Key1 = "before", Key2 = 0 }));
            await context.SaveChangesAsync();
        }

        BulkExecuteResult result;
        await using (var context = Fixture.CreateContext())
        {
            result = await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op
                    .Where(x => targets.Contains(x.Id))
                    .Set(x => x.Key1, "after")));
        }

        Assert.Equal(5000, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        Assert.Equal(5000, await verify.Items.CountAsync(x => x.Key1 == "after" && x.Id >= baseId && x.Id < baseId + 6000));
        Assert.Equal(1000, await verify.Items.CountAsync(x => x.Key1 == "before" && x.Id >= baseId && x.Id < baseId + 6000));
    }

    [Fact]
    public async Task Nullable_list_matches_null_and_value_rows_like_ef()
    {
        const int baseId = 60000;
        var ids = new List<int?> { 60001, null };

        await using (var context = Fixture.CreateContext())
        {
            await context.Items.Where(x => x.Id >= baseId && x.Id < baseId + 10).ExecuteDeleteAsync();
            context.Items.AddRange(Enumerable.Range(baseId, 10).Select(i => new Item
            {
                Id = i,
                Key1 = "n",
                ParentId = i is 60001 or 60002 ? 60001 : i == 60003 ? 99999 : null
            }));
            await context.SaveChangesAsync();
        }

        int bulkCount;
        await using (var context = Fixture.CreateContext())
        {
            var result = await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op
                    .Where(x => x.Id >= baseId && x.Id < baseId + 10 && ids.Contains(x.ParentId))
                    .Set(x => x.Key1, "hit")));
            bulkCount = result.TotalRowsAffected;
        }

        // Differential check against EF Core itself on the same seed.
        await using var verify = Fixture.CreateContext();
        var expected = await verify.Items.CountAsync(
            x => x.Id >= baseId && x.Id < baseId + 10 && ids.Contains(x.ParentId));
        Assert.Equal(expected, bulkCount);
        Assert.Equal(9, expected);
    }

    [Fact]
    public async Task Json_each_probe()
    {
        await using var context = Fixture.CreateContext();
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await context.Database.OpenConnectionAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM json_each('[1,2]')";
            var count = await command.ExecuteScalarAsync();
            Assert.Equal(2L, count);
        }
        finally
        {
            if (shouldClose)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }
}
