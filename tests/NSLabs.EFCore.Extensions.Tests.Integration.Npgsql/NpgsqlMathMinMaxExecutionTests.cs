using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Npgsql;

public class NpgsqlMathMinMaxExecutionTests : NpgsqlTestBase
{
    public NpgsqlMathMinMaxExecutionTests(NpgsqlFixture fixture) : base(fixture) { }

    [SkippableFact]
    public async Task Min_applies_cap_hit_and_cap_miss()
    {
        RequireDatabase();
        const int hitId = 9301;
        const int missId = 9302;
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
        Assert.Equal(10, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == hitId)).Key2);
        Assert.Equal(5, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == missId)).Key2);
    }

    [SkippableFact]
    public async Task Max_applies_floor()
    {
        RequireDatabase();
        const int id = 9303;
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
        Assert.Equal(10, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);
    }

    [SkippableFact]
    public async Task Min_nested_round_with_fractional_division_persists()
    {
        RequireDatabase();
        const string orderNo = "ORD-MIN-9304";
        decimal cap = 100m;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Orders.ExecuteDeleteAsync();
            ctx.Orders.Add(new Order { OrderNo = orderNo, Amount = 1000m, Status = OrderStatus.Pending });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Update<Order>(op => op
                .Where(x => x.OrderNo == orderNo)
                .Set(x => x.Amount, x => Math.Min(Math.Round(x.Amount / cap, 4), cap))));

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNo)).Amount);
    }

    [SkippableFact]
    public async Task Min_nested_round_with_column_cap_persists()
    {
        RequireDatabase();
        const string missNo = "ORD-MIN-9306";
        const string hitNo = "ORD-MIN-9307";
        decimal divisor = 100m;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Orders.ExecuteDeleteAsync();
            ctx.Orders.Add(new Order { OrderNo = missNo, Amount = 1000m, Status = OrderStatus.Pending });
            ctx.Orders.Add(new Order { OrderNo = hitNo, Amount = 99999999m, Status = OrderStatus.Pending });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b =>
        {
            b.Update<Order>(op => op
                .Where(x => x.OrderNo == missNo)
                .Set(x => x.Amount, x => Math.Min(Math.Round(x.Amount / divisor, 4), x.Amount)));
            b.Update<Order>(op => op
                .Where(x => x.OrderNo == hitNo)
                .Set(x => x.Amount, x => Math.Min(Math.Round(x.Amount / divisor, 4), x.Amount)));
        });

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == missNo)).Amount);
        Assert.Equal(999999.99m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == hitNo)).Amount);
    }

    [SkippableFact]
    public async Task Upsert_nested_round_with_literal_cap_persists()
    {
        RequireDatabase();
        const string missNo = "ORD-UPS-9308";
        const string hitNo = "ORD-UPS-9309";
        decimal divisor = 100m;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Orders.ExecuteDeleteAsync();
            ctx.Orders.Add(new Order { OrderNo = missNo, Amount = 1000m, Status = OrderStatus.Pending });
            ctx.Orders.Add(new Order { OrderNo = hitNo, Amount = 99999999m, Status = OrderStatus.Pending });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b =>
        {
            b.Upsert<Order>(u => u
                .MatchOn(x => x.OrderNo)
                .Update(x => x.Amount, x => Math.Min(Math.Round(x.Amount / divisor, 4), 9999.99m))
                .Insert(new Order { OrderNo = missNo, Amount = 0m, Status = OrderStatus.Pending }));
            b.Upsert<Order>(u => u
                .MatchOn(x => x.OrderNo)
                .Update(x => x.Amount, x => Math.Min(Math.Round(x.Amount / divisor, 4), 9999.99m))
                .Insert(new Order { OrderNo = hitNo, Amount = 0m, Status = OrderStatus.Pending }));
        });

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == missNo)).Amount);
        Assert.Equal(9999.99m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == hitNo)).Amount);
    }

    [SkippableFact]
    public async Task Upsert_computed_max_persists()
    {
        RequireDatabase();
        const int id = 9305;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key2 = 3 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Upsert<Item>(u => u
                .MatchOn(x => x.Id)
                .Update(x => x.Key2, x => Math.Max(x.Key2, 10))
                .Insert(new Item { Id = id, Key2 = 7 })));

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);
    }
}
