using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.SqlServer;

public class EntityStyleExecutionTests : SqlServerTestBase
{
    public EntityStyleExecutionTests(SqlServerFixture fixture) : base(fixture)
    {
    }

    [SkippableFact]
    public async Task Entity_rows_update_full_row_excluding_generated_columns()
    {
        RequireDatabase();
        const int id = 9301;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "old-key", Key2 = 1, Key3 = 2, Status = OrderStatus.Delivered, Active = false, CreatedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var detachedRow = new Item
        {
            Id = id,
            Key1 = "new-key",
            Key2 = 10,
            Key3 = 20,
            Status = OrderStatus.Shipped,
            Active = true,
            ParentId = 555,
            CreatedAt = DateTime.MaxValue
        };

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Items.BulkUpdateAsync(b => b.Add([detachedRow]));

            Assert.Equal(1, result.TotalRowsAffected);
            Assert.All(result.Operations, op => Assert.Equal("Item", op.EntityType));
        }

        await using var verify = Fixture.CreateContext();
        var reloaded = await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("new-key", reloaded.Key1);
        Assert.Equal(10, reloaded.Key2);
        Assert.Equal(20, reloaded.Key3);
        Assert.Equal(OrderStatus.Shipped, reloaded.Status);
        Assert.True(reloaded.Active);
        Assert.Equal(555, reloaded.ParentId);
        Assert.NotEqual(DateTime.MaxValue, reloaded.CreatedAt);
    }

    [SkippableFact]
    public async Task Custom_match_expression_matches_by_alternate_key_column()
    {
        RequireDatabase();

        var seededId = 0;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Customers.Add(new Customer { Code = "C-9401", Name = "Original", Active = true });
            await seed.SaveChangesAsync();
            seededId = seed.Customers.Local.Single().Id;
        }

        var detachedRow = new Customer { Code = "C-9401", Name = "Renamed", Active = false };

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Customers.BulkUpdateAsync(b => b
                .Add([detachedRow], (row, x) => x.Code == row.Code));

            Assert.Equal(1, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var customer = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == "C-9401");
        Assert.Equal("Renamed", customer.Name);
        Assert.False(customer.Active);
        Assert.Equal(seededId, customer.Id);
    }
}
