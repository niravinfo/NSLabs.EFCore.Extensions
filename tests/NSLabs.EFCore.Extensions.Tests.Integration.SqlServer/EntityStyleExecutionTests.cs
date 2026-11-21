using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.SqlServer;

public class EntityStyleExecutionTests : SqlServerTestBase
{
    public EntityStyleExecutionTests(SqlServerFixture fixture) : base(fixture)
    {
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task Composite_entity_row_custom_match_requires_every_term_and_preserves_per_row_counts()
    {
        RequireDatabase();
        const int firstId = 9701;
        const int secondId = 9702;
        var createdAt = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = firstId, Key1 = "p3-shared", Key2 = 1, Key3 = 10, CreatedAt = createdAt });
            seed.Items.Add(new Item { Id = secondId, Key1 = "p3-shared", Key2 = 2, Key3 = 20, CreatedAt = createdAt });
            await seed.SaveChangesAsync(cancellationToken);
        }

        var detachedRows = new[]
        {
            new Item { Id = 10701, Key1 = "p3-shared", Key2 = 1, Key3 = 101, CreatedAt = createdAt },
            new Item { Id = 10702, Key1 = "p3-shared", Key2 = 2, Key3 = 202, CreatedAt = createdAt }
        };

        BulkExecuteResult result;
        await using (var context = Fixture.CreateContext())
        {
            result = await context.BulkExecuteAsync(
                b => b.Update<Item>(detachedRows, (row, x) => x.Key1 == row.Key1 && row.Key2 == x.Key2),
                cancellationToken);
        }

        Assert.Equal(2, result.TotalRowsAffected);
        Assert.Collection(
            result.Operations,
            operation => Assert.Equal(1, operation.RowsAffected),
            operation => Assert.Equal(1, operation.RowsAffected));

        await using var verify = Fixture.CreateContext();
        var items = await verify.Items.AsNoTracking()
            .Where(x => x.Id == firstId || x.Id == secondId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        Assert.Equal(101, items[0].Key3);
        Assert.Equal(202, items[1].Key3);
    }
}
