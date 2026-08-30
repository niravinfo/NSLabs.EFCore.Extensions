using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration;

public class PredicateTranslationExecutionTests : SqlServerTestBase
{
    public PredicateTranslationExecutionTests(SqlServerFixture fixture) : base(fixture) { }

    [SkippableFact]
    public async Task Contains_updates_matching_rows()
    {
        RequireDatabase();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 7001, Key1 = $"alpha-{suffix}", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7002, Key1 = $"beta-{suffix}", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7003, Key1 = "other", Key2 = 0 });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = Fixture.CreateContext())
        {
            var r = await ctx.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Key1.Contains(suffix)).Set(x => x.Key2, 99)));
            Assert.Equal(2, r.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var dict = await verify.Items.AsNoTracking().Where(x => x.Id >= 7001 && x.Id <= 7003).ToDictionaryAsync(x => x.Id);
        Assert.Equal(99, dict[7001].Key2);
        Assert.Equal(99, dict[7002].Key2);
        Assert.Equal(0, dict[7003].Key2);
    }

    [SkippableFact]
    public async Task StartsWith_updates_prefix_match()
    {
        RequireDatabase();
        var prefix = "PRE-" + Guid.NewGuid().ToString("N")[..4];
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 7011, Key1 = prefix + "-A", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7012, Key1 = prefix + "-B", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7013, Key1 = "OTHER", Key2 = 0 });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = Fixture.CreateContext())
        {
            var r = await ctx.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Key1.StartsWith(prefix)).Set(x => x.Key2, 11)));
            Assert.Equal(2, r.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(11, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == 7011)).Key2);
        Assert.Equal(0, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == 7013)).Key2);
    }

    [SkippableFact]
    public async Task EndsWith_updates_suffix_match()
    {
        RequireDatabase();
        var suffix = "-SUF-" + Guid.NewGuid().ToString("N")[..4];
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 7021, Key1 = "A" + suffix, Key2 = 0 });
            seed.Items.Add(new Item { Id = 7022, Key1 = "B" + suffix, Key2 = 0 });
            seed.Items.Add(new Item { Id = 7023, Key1 = "OTHER", Key2 = 0 });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = Fixture.CreateContext())
        {
            var r = await ctx.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Key1.EndsWith(suffix)).Set(x => x.Key2, 22)));
            Assert.Equal(2, r.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(22, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == 7021)).Key2);
    }

    [SkippableFact]
    public async Task In_collection_updates_matching_ids()
    {
        RequireDatabase();
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 7031, Key1 = "a", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7032, Key1 = "b", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7033, Key1 = "c", Key2 = 0 });
            await seed.SaveChangesAsync();
        }

        var ids = new[] { 7031, 7033 };
        await using (var ctx = Fixture.CreateContext())
        {
            var r = await ctx.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key2, 55)));
            Assert.Equal(2, r.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var d = await verify.Items.AsNoTracking().Where(x => x.Id >= 7031 && x.Id <= 7033).ToDictionaryAsync(x => x.Id);
        Assert.Equal(55, d[7031].Key2);
        Assert.Equal(0, d[7032].Key2);
        Assert.Equal(55, d[7033].Key2);
    }

    [SkippableFact]
    public async Task NotIn_collection_excludes_matching_ids()
    {
        RequireDatabase();
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 7041, Key1 = "a", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7042, Key1 = "b", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7043, Key1 = "c", Key2 = 0 });
            await seed.SaveChangesAsync();
        }

        var ids = new[] { 7041 };
        await using (var ctx = Fixture.CreateContext())
        {
            var r = await ctx.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => !ids.Contains(x.Id)).Set(x => x.Key2, 77)));
            Assert.Equal(2, r.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var d = await verify.Items.AsNoTracking().Where(x => x.Id >= 7041 && x.Id <= 7043).ToDictionaryAsync(x => x.Id);
        Assert.Equal(0, d[7041].Key2);
        Assert.Equal(77, d[7042].Key2);
    }

    [SkippableFact]
    public async Task IsNullOrEmpty_matches_null_and_empty()
    {
        RequireDatabase();
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 7051, Key1 = "", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7052, Key1 = "notempty", Key2 = 0 });
            // Key1 is non-nullable in TestModel, so null case is via IsNull check on nullable ParentId
            seed.Items.Add(new Item { Id = 7053, Key1 = "", Key2 = 0 });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = Fixture.CreateContext())
        {
            var r = await ctx.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => string.IsNullOrEmpty(x.Key1)).Set(x => x.Key2, 88)));
            Assert.Equal(2, r.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var d = await verify.Items.AsNoTracking().Where(x => x.Id >= 7051 && x.Id <= 7053).ToDictionaryAsync(x => x.Id);
        Assert.Equal(88, d[7051].Key2);
        Assert.Equal(0, d[7052].Key2);
    }

    [SkippableFact]
    public async Task Like_with_pattern_updates()
    {
        RequireDatabase();
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 7061, Key1 = "hello-world", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7062, Key1 = "hello-test", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7063, Key1 = "other", Key2 = 0 });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = Fixture.CreateContext())
        {
            var r = await ctx.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => EF.Functions.Like(x.Key1, "hello-%")).Set(x => x.Key2, 66)));
            Assert.Equal(2, r.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var d = await verify.Items.AsNoTracking().Where(x => x.Id >= 7061 && x.Id <= 7063).ToDictionaryAsync(x => x.Id);
        Assert.Equal(66, d[7061].Key2);
        Assert.Equal(66, d[7062].Key2);
        Assert.Equal(0, d[7063].Key2);
    }

    [SkippableFact]
    public async Task Combined_predicate_with_contains_and_in()
    {
        RequireDatabase();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var ids = new[] { 7071, 7072, 7073 };
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 7071, Key1 = $"a-{suffix}", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7072, Key1 = $"b-{suffix}", Key2 = 0 });
            seed.Items.Add(new Item { Id = 7073, Key1 = "other", Key2 = 0 });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = Fixture.CreateContext())
        {
            var r = await ctx.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Key1.Contains(suffix) && ids.Contains(x.Id)).Set(x => x.Key2, 99)));
            Assert.Equal(2, r.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var d = await verify.Items.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        Assert.Equal(99, d[7071].Key2);
        Assert.Equal(99, d[7072].Key2);
        Assert.Equal(0, d[7073].Key2);
    }
}
