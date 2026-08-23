using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration;

public class TransactionAndSemanticsTests : SqlServerTestBase
{
    public TransactionAndSemanticsTests(SqlServerFixture fixture) : base(fixture)
    {
    }

    [SkippableFact]
    public async Task Ambient_user_transaction_commit_persists()
    {
        RequireDatabase();
        const int id = 9201;

        await using var context = Fixture.CreateContext();

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "start" });
            await seed.SaveChangesAsync();
        }

        await context.Database.BeginTransactionAsync();

        await context.BulkExecuteAsync(b => b
            .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, "committed")));

        await context.Database.CommitTransactionAsync();

        await using var verify = Fixture.CreateContext();
        Assert.Equal("committed", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key1);
    }

    [SkippableFact]
    public async Task Ambient_user_transaction_rollback_discards_batch()
    {
        RequireDatabase();
        const int id = 9202;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "start" });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.Database.BeginTransactionAsync();

            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, "discarded")));

            await context.Database.RollbackTransactionAsync();
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("start", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key1);
    }

    [SkippableFact]
    public async Task Enum_converter_roundtrips_through_predicate_and_set()
    {
        RequireDatabase();
        const string orderNo = "ORD-9203";

        await using (var seed = Fixture.CreateContext())
        {
            seed.Orders.Add(new Order { OrderNo = orderNo, Amount = 1m, Status = OrderStatus.Pending });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.BulkExecuteAsync(b => b
                .Update<Order>(op => op
                    .Where(x => x.Status == OrderStatus.Pending && x.OrderNo == orderNo)
                    .Set(x => x.Status, OrderStatus.Shipped)));

            Assert.Equal(1, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(OrderStatus.Shipped, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNo)).Status);
    }

    [SkippableFact]
    public async Task Null_predicate_targets_only_null_rows()
    {
        RequireDatabase();
        const int nullRowId = 9204;
        const int nonNullRowId = 9205;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = nullRowId, ParentId = null, Key3 = 0 });
            seed.Items.Add(new Item { Id = nonNullRowId, ParentId = 123, Key3 = 0 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op
                    .Where(x => x.ParentId == null)
                    .Set(x => x.Key3, 77)));

            Assert.Equal(1, result.Operations[0].RowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(77, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == nullRowId)).Key3);
        Assert.Equal(0, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == nonNullRowId)).Key3);
    }

    [SkippableFact]
    public async Task Tph_discriminator_scopes_updates_to_derived_type()
    {
        RequireDatabase();

        await using (var seed = Fixture.CreateContext())
        {
            seed.Add(new Cat { PetId = 9206, Name = "Rex", LivesLeft = 9 });
            seed.Add(new Dog { PetId = 9207, Name = "Rex", Breed = "Beagle" });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.BulkExecuteAsync(b => b
                .Update<Cat>(op => op
                    .Where(x => x.Name == "Rex")
                    .Set(x => x.LivesLeft, 8)));

            Assert.Equal(1, result.Operations[0].RowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var cat = await verify.Set<Cat>().AsNoTracking().SingleAsync(x => x.PetId == 9206);
        var dog = await verify.Set<Dog>().AsNoTracking().SingleAsync(x => x.PetId == 9207);
        Assert.Equal(8, cat.LivesLeft);
        Assert.Equal("Beagle", dog.Breed);
    }

    [SkippableFact]
    public async Task Sequential_overlap_semantics_match_documented_behavior()
    {
        RequireDatabase();
        const int firstId = 9208;
        const int secondId = 9209;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = firstId, Key1 = "Old", Key3 = 0 });
            seed.Items.Add(new Item { Id = secondId, Key1 = "Old", Key3 = 0 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Key1 == "Old").Set(x => x.Key1, "New"));
                b.Update<Item>(op => op.Where(x => x.Key1 == "Old").Set(x => x.Key3, 5));
            });

            Assert.Equal(2, result.Operations[0].RowsAffected);
            Assert.Equal(0, result.Operations[1].RowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var rows = await verify.Items.AsNoTracking().Where(x => x.Id == firstId || x.Id == secondId).ToListAsync();
        Assert.All(rows, row =>
        {
            Assert.Equal("New", row.Key1);
            Assert.Equal(0, row.Key3);
        });
    }
}
