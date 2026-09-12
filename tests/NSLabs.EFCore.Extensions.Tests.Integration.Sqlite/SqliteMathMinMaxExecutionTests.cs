using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Sqlite;

public class SqliteMathMinMaxExecutionTests : SqliteTestBase
{
    public SqliteMathMinMaxExecutionTests(SqliteFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Min_applies_cap_hit_and_cap_miss()
    {
        const int hitId = 9501;
        const int missId = 9502;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = hitId, Key2 = 50 });
            ctx.Items.Add(new Item { Id = missId, Key2 = 5 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == hitId).Set(x => x.Key2, x => Math.Min(x.Key2, 10)));
            b.Update<Item>(op => op.Where(x => x.Id == missId).Set(x => x.Key2, x => Math.Min(x.Key2, 10)));
        });

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10, (await verify.Items.SingleAsync(x => x.Id == hitId)).Key2);
        Assert.Equal(5, (await verify.Items.SingleAsync(x => x.Id == missId)).Key2);
    }

    [Fact]
    public async Task Max_applies_floor()
    {
        const int id = 9503;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key2 = 3 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => Math.Max(x.Key2, 10))));

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10, (await verify.Items.SingleAsync(x => x.Id == id)).Key2);
    }

    // Int domain: SQLite binds decimal parameters as TEXT, so min()/max() over decimals
    // compare across storage classes (documented EF-on-SQLite behavior, not ours).
    // Fractional division is covered by golden-SQL tests; values here stay integral.
    [Fact]
    public async Task Min_nested_round_with_division_cap_miss_persists()
    {
        const int id = 9504;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key2 = 7 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == id)
                .Set(x => x.Key2, x => Math.Min((int)Math.Round((double)x.Key2 / 3), 10))));

        await using var verify = Fixture.CreateContext();
        // 7/3 = 2.333 -> ROUND = 2 (cap miss) -> 2
        Assert.Equal(2, (await verify.Items.SingleAsync(x => x.Id == id)).Key2);
    }

    [Fact]
    public async Task Min_nested_round_with_division_cap_hit_persists()
    {
        const int id = 9505;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key2 = 999 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == id)
                .Set(x => x.Key2, x => Math.Min((int)Math.Round((double)x.Key2 / 3), 10))));

        await using var verify = Fixture.CreateContext();
        // 999/3 = 333 (cap hit) -> 10
        Assert.Equal(10, (await verify.Items.SingleAsync(x => x.Id == id)).Key2);
    }

    [Fact]
    public async Task Min_inside_ternary_persists()
    {
        const int id = 9506;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key2 = 50 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == id)
                .Set(x => x.Key2, x => x.Key2 > 0 ? Math.Min(x.Key2, 10) : x.Key2)));

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10, (await verify.Items.SingleAsync(x => x.Id == id)).Key2);
    }

    [Fact]
    public async Task Min_max_nesting_persists()
    {
        const int id = 9507;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key2 = 50 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == id)
                .Set(x => x.Key2, x => Math.Max(Math.Min(x.Key2, 10), 1))));

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10, (await verify.Items.SingleAsync(x => x.Id == id)).Key2);
    }

    [Fact]
    public async Task Min_nested_round_with_column_cap_persists()
    {
        const int hitId = 9509;
        const int missId = 9510;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = hitId, Key2 = 999, Key3 = 3 });
            ctx.Items.Add(new Item { Id = missId, Key2 = 7, Key3 = 3 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == hitId).Set(x => x.Key2, x => Math.Min((int)Math.Round((double)x.Key2 / x.Key3, 4), x.Key3)));
            b.Update<Item>(op => op.Where(x => x.Id == missId).Set(x => x.Key2, x => Math.Min((int)Math.Round((double)x.Key2 / x.Key3, 4), x.Key3)));
        });

        await using var verify = Fixture.CreateContext();
        // 999/3 = 333 (cap hit against Key3=3) -> 3; 7/3 = 2.333 -> ROUND = 2 (cap miss) -> 2
        Assert.Equal(3, (await verify.Items.SingleAsync(x => x.Id == hitId)).Key2);
        Assert.Equal(2, (await verify.Items.SingleAsync(x => x.Id == missId)).Key2);
    }

    [Fact]
    public async Task Upsert_nested_round_with_literal_cap_persists()
    {
        const int id = 9511;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key2 = 999, Key3 = 3 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Upsert<Item>(u => u
                .MatchOn(x => x.Id)
                .Update(x => x.Key2, x => Math.Min((int)Math.Round((double)x.Key2 / x.Key3, 4), 10))
                .Insert(new Item { Id = id, Key2 = 7, Key3 = 3 })));

        await using var verify = Fixture.CreateContext();
        // matched row: 999/3 = 333 (cap hit) -> 10
        Assert.Equal(10, (await verify.Items.SingleAsync(x => x.Id == id)).Key2);
    }

    [Fact]
    public async Task Upsert_computed_min_persists()
    {
        const int id = 9508;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key2 = 50 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Upsert<Item>(u => u
                .MatchOn(x => x.Id)
                .Update(x => x.Key2, x => Math.Min(x.Key2, 10))
                .Insert(new Item { Id = id, Key2 = 7 })));

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10, (await verify.Items.SingleAsync(x => x.Id == id)).Key2);
    }
}
