using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Sqlite;

public class SqliteComputedSetExecutionTests : SqliteTestBase
{
    public SqliteComputedSetExecutionTests(SqliteFixture fixture) : base(fixture) { }

    [Fact]
    public async Task String_concat_plus_persists()
    {
        const int id = 9401;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key1 = "hello" });
            await ctx.SaveChangesAsync();
        }
        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, x => x.Key1 + "_suf")));
        await using var verify = Fixture.CreateContext();
        Assert.Equal("hello_suf", (await verify.Items.SingleAsync(x => x.Id == id)).Key1);
    }

    [Fact]
    public async Task Upper_and_lower_and_trim_persists()
    {
        const int id = 9402;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key1 = "  Hello  " });
            await ctx.SaveChangesAsync();
        }
        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, x => x.Key1.Trim().ToUpper())));
        await using var verify = Fixture.CreateContext();
        Assert.Equal("HELLO", (await verify.Items.SingleAsync(x => x.Id == id)).Key1);
    }

    [Fact]
    public async Task Length_and_substring_persists()
    {
        const int id = 9403;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key1 = "abcdef", Key2 = 0 });
            await ctx.SaveChangesAsync();
        }
        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => x.Key1.Length))
            .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, x => x.Key1.Substring(1, 2))));
        await using var verify = Fixture.CreateContext();
        var item = await verify.Items.SingleAsync(x => x.Id == id);
        // first op sets Key2=6, second sets Key1="bc"
        Assert.Equal(6, item.Key2);
        Assert.Equal("bc", item.Key1);
    }

    [Fact]
    public async Task Coalesce_persists()
    {
        const int id = 9404;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, ParentId = null, Key2 = 0 });
            await ctx.SaveChangesAsync();
        }
        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => x.ParentId ?? 5)));
        await using var verify = Fixture.CreateContext();
        Assert.Equal(5, (await verify.Items.SingleAsync(x => x.Id == id)).Key2);
    }

    [Fact]
    public async Task Conditional_case_persists()
    {
        const int id = 9405;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key2 = 10 });
            await ctx.SaveChangesAsync();
        }
        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => x.Key2 > 5 ? x.Key2 + 1 : x.Key2 - 1)));
        await using var verify = Fixture.CreateContext();
        Assert.Equal(11, (await verify.Items.SingleAsync(x => x.Id == id)).Key2);
    }

    [Fact]
    public async Task Math_abs_and_round_persists()
    {
        const int id = 9406;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key2 = -5 });
            await ctx.SaveChangesAsync();
        }
        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => Math.Abs(x.Key2))));
        await using var verify = Fixture.CreateContext();
        Assert.Equal(5, (await verify.Items.SingleAsync(x => x.Id == id)).Key2);
    }

    [Fact]
    public async Task Computed_with_arithmetic_persists()
    {
        const int id = 9407;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key2 = 3, Key3 = 4 });
            await ctx.SaveChangesAsync();
        }
        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => (x.Key2 + x.Key3) * 2)));
        await using var verify = Fixture.CreateContext();
        Assert.Equal(14, (await verify.Items.SingleAsync(x => x.Id == id)).Key2);
    }
}
