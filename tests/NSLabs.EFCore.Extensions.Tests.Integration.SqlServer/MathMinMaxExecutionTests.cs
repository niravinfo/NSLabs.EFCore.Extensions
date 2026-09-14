using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.SqlServer;

/// <summary>
/// Execution tests for Math.Min/Max computed SET expressions (LEAST/GREATEST).
/// SQL Server requires compatibility level 160+, inherited from EF Core's own
/// UseCompatibilityLevel (the library has no compat knob of its own).
/// Golden-SQL tests in MathMinMaxGoldenSqlTests.cs verify emission and the compat gate.
/// </summary>
public class MathMinMaxExecutionTests : SqlServerTestBase
{
    public MathMinMaxExecutionTests(SqlServerFixture fixture) : base(fixture) { }

    private TestDbContext CreateCompat160Context()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(Fixture.ConnectionString, b => b.UseCompatibilityLevel(160))
            .Options;
        return new IntegrationTestDbContext(options);
    }

    [Fact]
    public async Task Default_ef_compat_throws_for_min()
    {
        // Generation fails before any database round-trip, so no container is required.
        await using var context = Fixture.CreateContext();
        await Assert.ThrowsAsync<NotSupportedException>(() => context.BulkExecuteAsync(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => Math.Min(x.Key2, 10)))));
    }

    [Fact]
    public async Task Min_applies_cap_hit_and_cap_miss()
    {
        RequireDatabase();
        const int hitId = 20201;
        const int missId = 20202;
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.Where(x => x.Id == hitId || x.Id == missId).ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = hitId, Key2 = 50 });
            seed.Items.Add(new Item { Id = missId, Key2 = 5 });
            await seed.SaveChangesAsync();
        }

        await using (var context = CreateCompat160Context())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == hitId).Set(x => x.Key2, x => Math.Min(x.Key2, 10)));
                b.Update<Item>(op => op.Where(x => x.Id == missId).Set(x => x.Key2, x => Math.Min(x.Key2, 10)));
            });
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == hitId)).Key2);
        Assert.Equal(5, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == missId)).Key2);
    }

    [Fact]
    public async Task Max_applies_floor()
    {
        RequireDatabase();
        const int id = 20203;
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.Where(x => x.Id == id).ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = id, Key2 = 3 });
            await seed.SaveChangesAsync();
        }

        await using (var context = CreateCompat160Context())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => Math.Max(x.Key2, 10))));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);
    }

    [Fact]
    public async Task Min_nested_round_with_fractional_division_persists()
    {
        RequireDatabase();
        const string orderNo = "ORD-MIN-20204";
        decimal cap = 100m;
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Orders.Where(x => x.OrderNo == orderNo).ExecuteDeleteAsync();
            seed.Orders.Add(new Order { OrderNo = orderNo, Amount = 1000m, Status = OrderStatus.Pending });
            await seed.SaveChangesAsync();
        }

        await using (var context = CreateCompat160Context())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Order>(op => op
                    .Where(x => x.OrderNo == orderNo)
                    .Set(x => x.Amount, x => Math.Min(Math.Round(x.Amount / cap, 4), cap))));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNo)).Amount);
    }

    [Fact]
    public async Task Min_nested_round_with_column_cap_persists()
    {
        RequireDatabase();
        const string missNo = "ORD-MIN-20206";
        const string hitNo = "ORD-MIN-20207";
        decimal divisor = 100m;
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Orders.Where(x => x.OrderNo == missNo || x.OrderNo == hitNo).ExecuteDeleteAsync();
            seed.Orders.Add(new Order { OrderNo = missNo, Amount = 1000m, Status = OrderStatus.Pending });
            seed.Orders.Add(new Order { OrderNo = hitNo, Amount = 99999999m, Status = OrderStatus.Pending });
            await seed.SaveChangesAsync();
        }

        await using (var context = CreateCompat160Context())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Order>(op => op
                    .Where(x => x.OrderNo == missNo)
                    .Set(x => x.Amount, x => Math.Min(Math.Round(x.Amount / divisor, 4), x.Amount)));

                b.Update<Order>(op => op
                    .Where(x => x.OrderNo == hitNo)
                    .Set(x => x.Amount, x => Math.Min(Math.Round(x.Amount / divisor, 4), x.Amount)));
            });
        }

        await using var verify = Fixture.CreateContext();
        // 1000/100 = 10 (cap miss against Amount) -> 10
        Assert.Equal(10m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == missNo)).Amount);
        // 99999999/100 = 999999.99 (cap hit) -> 999999.99
        Assert.Equal(999999.99m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == hitNo)).Amount);
    }

    [Fact]
    public async Task Upsert_nested_round_with_literal_cap_persists()
    {
        RequireDatabase();
        const string missNo = "ORD-UPS-20208";
        const string hitNo = "ORD-UPS-20209";
        decimal divisor = 100m;
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Orders.Where(x => x.OrderNo == missNo || x.OrderNo == hitNo).ExecuteDeleteAsync();
            seed.Orders.Add(new Order { OrderNo = missNo, Amount = 1000m, Status = OrderStatus.Pending });
            seed.Orders.Add(new Order { OrderNo = hitNo, Amount = 99999999m, Status = OrderStatus.Pending });
            await seed.SaveChangesAsync();
        }

        await using (var context = CreateCompat160Context())
        {
            await context.BulkExecuteAsync(b =>
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
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == missNo)).Amount);
        Assert.Equal(9999.99m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == hitNo)).Amount);
    }

    [Fact]
    public async Task Upsert_computed_max_persists()
    {
        RequireDatabase();
        const int id = 20205;
        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.Where(x => x.Id == id).ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = id, Key2 = 3 });
            await seed.SaveChangesAsync();
        }

        await using (var context = CreateCompat160Context())
        {
            await context.BulkExecuteAsync(b => b
                .Upsert<Item>(u => u
                    .MatchOn(x => x.Id)
                    .Update(x => x.Key2, x => Math.Max(x.Key2, 10))
                    .Insert(new Item { Id = id, Key2 = 7 })));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);
    }
}
