using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Npgsql;

public class NpgsqlLargeListExecutionTests : NpgsqlTestBase
{
    public NpgsqlLargeListExecutionTests(NpgsqlFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Update_with_5000_ids_matches_5000_rows()
    {
        RequireDatabase();
        const int baseId = 70000;
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
    public async Task String_and_converted_lists_bind_typed_arrays()
    {
        RequireDatabase();
        const int baseId = 80000;

        await using (var context = Fixture.CreateContext())
        {
            await context.Items.Where(x => x.Id >= baseId && x.Id < baseId + 4).ExecuteDeleteAsync();
            context.Items.AddRange(new[]
            {
                new Item { Id = baseId, Key1 = "alpha", Status = OrderStatus.Pending },
                new Item { Id = baseId + 1, Key1 = "beta", Status = OrderStatus.Shipped },
                new Item { Id = baseId + 2, Key1 = "gamma", Status = OrderStatus.Delivered },
                new Item { Id = baseId + 3, Key1 = "delta", Status = OrderStatus.Pending }
            });
            await context.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var keys = new[] { "alpha", "gamma" };
            var statuses = new[] { OrderStatus.Pending, OrderStatus.Delivered };
            var result = await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op
                    .Where(x => x.Id >= baseId && x.Id < baseId + 4
                        && keys.Contains(x.Key1)
                        && statuses.Contains(x.Status))
                    .Set(x => x.Key3, 7)));
            Assert.Equal(2, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(2, await verify.Items.CountAsync(x => x.Key3 == 7 && x.Id >= baseId && x.Id < baseId + 4));
    }

    [Fact]
    public async Task Update_with_50000_ids_stays_single_statement()
    {
        RequireDatabase();
        const int baseId = 900000;

        await using (var context = Fixture.CreateContext())
        {
            // Set-based seed: 50k rows in one round trip (generate_series).
            await context.Database.ExecuteSqlRawAsync(
                """DELETE FROM "Items" WHERE "Id" >= {0}; INSERT INTO "Items" ("Id", "Key1", "Key2", "Key3", "Status", "Active", "CreatedAt") SELECT g, 'smoke', 0, 0, 0, FALSE, now() FROM generate_series({0}, {1}) g""",
                baseId, baseId + 49999);
        }

        var targets = Enumerable.Range(baseId, 50000).ToList();
        BulkExecuteResult result;
        await using (var context = Fixture.CreateContext())
        {
            result = await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op
                    .Where(x => targets.Contains(x.Id))
                    .Set(x => x.Key3, 9)));
        }

        Assert.Equal(50000, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        Assert.Equal(50000, await verify.Items.CountAsync(x => x.Key3 == 9 && x.Id >= baseId));
    }

    [Fact]
    public async Task Nullable_list_matches_null_and_value_rows_like_ef()
    {
        RequireDatabase();
        const int baseId = 81000;
        var ids = new List<int?> { 81001, null };

        await using (var context = Fixture.CreateContext())
        {
            await context.Items.Where(x => x.Id >= baseId && x.Id < baseId + 10).ExecuteDeleteAsync();
            context.Items.AddRange(Enumerable.Range(baseId, 10).Select(i => new Item
            {
                Id = i,
                Key1 = "n",
                ParentId = i is 81001 or 81002 ? 81001 : i == 81003 ? 99999 : null
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
}
