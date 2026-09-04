using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Sqlite;

public class SqlitePredicateTranslationExecutionTests : SqliteTestBase
{
    public SqlitePredicateTranslationExecutionTests(SqliteFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Contains_with_like_escapes_correctly()
    {
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = 9701, Key1 = "a%b_c" });
            ctx.Items.Add(new Item { Id = 9702, Key1 = "abc" });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Key1.Contains("a%b")).Set(x => x.Key2, 1)));

        await using var verify = Fixture.CreateContext();
        Assert.Equal(1, (await verify.Items.SingleAsync(x => x.Id == 9701)).Key2);
        Assert.Equal(0, (await verify.Items.SingleAsync(x => x.Id == 9702)).Key2);
    }

    [Fact]
    public async Task In_clause_filters_correctly()
    {
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = 9711, Key1 = "a" });
            ctx.Items.Add(new Item { Id = 9712, Key1 = "b" });
            ctx.Items.Add(new Item { Id = 9713, Key1 = "c" });
            await ctx.SaveChangesAsync();
        }

        var ids = new[] { 9711, 9713 };
        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key2, 7)));

        await using var verify = Fixture.CreateContext();
        Assert.Equal(7, (await verify.Items.SingleAsync(x => x.Id == 9711)).Key2);
        Assert.Equal(0, (await verify.Items.SingleAsync(x => x.Id == 9712)).Key2);
        Assert.Equal(7, (await verify.Items.SingleAsync(x => x.Id == 9713)).Key2);
    }

    [Fact]
    public async Task IsNullOrEmpty_filters_correctly()
    {
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = 9721, Key1 = "" });
            ctx.Items.Add(new Item { Id = 9722, Key1 = "x" });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => string.IsNullOrEmpty(x.Key1)).Set(x => x.Key2, 3)));

        await using var verify = Fixture.CreateContext();
        Assert.Equal(3, (await verify.Items.SingleAsync(x => x.Id == 9721)).Key2);
        Assert.Equal(0, (await verify.Items.SingleAsync(x => x.Id == 9722)).Key2);
    }

    [Fact]
    public async Task StartsWith_filters_correctly()
    {
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = 9731, Key1 = "hello" });
            ctx.Items.Add(new Item { Id = 9732, Key1 = "world" });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Key1.StartsWith("hel")).Set(x => x.Key2, 9)));

        await using var verify = Fixture.CreateContext();
        Assert.Equal(9, (await verify.Items.SingleAsync(x => x.Id == 9731)).Key2);
        Assert.Equal(0, (await verify.Items.SingleAsync(x => x.Id == 9732)).Key2);
    }

    [Fact]
    public async Task Boolean_predicate_filters_correctly()
    {
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = 9741, Active = true, Key2 = 0 });
            ctx.Items.Add(new Item { Id = 9742, Active = false, Key2 = 0 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Active).Set(x => x.Key2, 11)));

        await using var verify = Fixture.CreateContext();
        Assert.Equal(11, (await verify.Items.SingleAsync(x => x.Id == 9741)).Key2);
        Assert.Equal(0, (await verify.Items.SingleAsync(x => x.Id == 9742)).Key2);
    }
}
