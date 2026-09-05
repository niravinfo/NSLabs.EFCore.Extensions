using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Sqlite;

public class SqliteUpsertExecutionTests : SqliteTestBase
{
    public SqliteUpsertExecutionTests(SqliteFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Upsert_inserts_when_not_exists_and_updates_when_exists()
    {
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Customers.ExecuteDeleteAsync();
            ctx.Customers.Add(new Customer { Code = "A", Name = "Old", Active = true });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        var result = await ctx2.BulkExecuteAsync(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .Insert(new[]
                {
                    new Customer { Code = "A", Name = "New", Active = false },
                    new Customer { Code = "B", Name = "Inserted", Active = true }
                })));

        Assert.Equal(2, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        var a = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == "A");
        Assert.Equal("New", a.Name);
        Assert.False(a.Active);
        var b = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == "B");
        Assert.Equal("Inserted", b.Name);
    }

    [Fact]
    public async Task Upsert_with_guard_skips_update_when_guard_false()
    {
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Customers.ExecuteDeleteAsync();
            ctx.Customers.Add(new Customer { Code = "G", Name = "Keep", Active = false });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        var result = await ctx2.BulkExecuteAsync(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .UpdateWhen(x => x.Active)
                .Update(x => x.Name, "Changed")
                .Insert(new Customer { Code = "G", Name = "Changed", Active = true })));

        // Guard false -> 0 rows affected, existing row untouched, no insert attempted.
        Assert.Equal(0, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        var g = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == "G");
        Assert.Equal("Keep", g.Name);
        Assert.False(g.Active);
    }

    [Fact]
    public async Task Upsert_with_guard_true_applies_update()
    {
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Customers.ExecuteDeleteAsync();
            ctx.Customers.Add(new Customer { Code = "GT", Name = "Before", Active = true });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        var result = await ctx2.BulkExecuteAsync(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .UpdateWhen(x => x.Active)
                .Update(x => x.Name, "Changed")
                .Insert(new Customer { Code = "GT", Name = "Changed", Active = false })));

        Assert.Equal(1, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        var g = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == "GT");
        Assert.Equal("Changed", g.Name);
        // Explicit Update wrote only Name; the Insert row's Active=false must NOT leak onto the match.
        Assert.True(g.Active);
    }

    [Fact]
    public async Task Explicit_update_constants_apply_on_match_only()
    {
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Customers.ExecuteDeleteAsync();
            ctx.Customers.Add(new Customer { Code = "E", Name = "Before", Active = true });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        var result = await ctx2.BulkExecuteAsync(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .Update(x => x.Active, false)
                .Insert(new Customer { Code = "E", Name = "IgnoredOnMatch", Active = false })));

        Assert.Equal(1, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        var reloaded = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == "E");
        Assert.False(reloaded.Active);
        Assert.Equal("Before", reloaded.Name);
    }

    [Fact]
    public async Task Upsert_with_computed_set_persists()
    {
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Orders.ExecuteDeleteAsync();
            ctx.Orders.Add(new Order { OrderNo = "O-100", Amount = 10m, Status = OrderStatus.Pending });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Upsert<Order>(u => u
                .MatchOn(x => x.OrderNo)
                .Update(x => x.Amount, x => x.Amount * 2m)
                .Insert(new Order { OrderNo = "O-100", Amount = 999m, Status = OrderStatus.Shipped })));

        await using var verify = Fixture.CreateContext();
        var o = await verify.Orders.SingleAsync(x => x.OrderNo == "O-100");
        Assert.Equal(20m, o.Amount);
    }

    [Fact]
    public async Task Upsert_zero_rows_is_noop()
    {
        await using var ctx = Fixture.CreateContext();
        var result = await ctx.BulkExecuteAsync(b => b
            .Upsert<Customer>(u => u.MatchOn(x => x.Code).Insert(Array.Empty<Customer>())));
        Assert.Equal(0, result.TotalRowsAffected);
    }

    [Fact]
    public async Task Upsert_duplicate_key_throws_before_execution()
    {
        await using var ctx = Fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.BulkExecuteAsync(b => b
            .Upsert<Customer>(u => u.MatchOn(x => x.Code).Insert(new[]
            {
                new Customer { Code = "DUP" },
                new Customer { Code = "DUP" }
            }))));
        Assert.Contains("Duplicate", ex.Message);
    }
}
