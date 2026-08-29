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
            // Other tests seed Items whose ParentId defaults to null; clear them so the
            // affected-row count below is exact regardless of execution order.
            await seed.Items.ExecuteDeleteAsync();
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

    [SkippableFact]
    public async Task No_transaction_bulk_execute_persists_without_explicit_transaction()
    {
        RequireDatabase();
        const int id = 9210;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "before" });
            await seed.SaveChangesAsync();
        }

        await using var context = Fixture.CreateContext();
        var result = await context.BulkExecuteAsync(b =>
            b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, "after")));

        Assert.Equal(1, result.TotalRowsAffected);
        Assert.Equal(1, result.Operations[0].RowsAffected);

        await using var verify = Fixture.CreateContext();
        Assert.Equal("after", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key1);
    }

    [SkippableFact]
    public async Task User_transaction_mixed_bulk_and_savechanges_commit_atomic()
    {
        RequireDatabase();
        const int updateId = 9211;
        const int insertItemId = 9212;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = updateId, Key1 = "start" });
            await seed.SaveChangesAsync();
        }

        await using var context = Fixture.CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        // Bulk update + regular SaveChanges should both commit atomically when wrapped.
        context.Items.Add(new Item { Id = insertItemId, Key1 = "new" });
        await context.SaveChangesAsync();

        await context.BulkExecuteAsync(b =>
            b.Update<Item>(op => op.Where(x => x.Id == updateId).Set(x => x.Key1, "committed")));

        await tx.CommitAsync();

        await using var verify = Fixture.CreateContext();
        Assert.Equal("committed", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == updateId)).Key1);
        Assert.NotNull(await verify.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == insertItemId));
    }

    [SkippableFact]
    public async Task User_transaction_mixed_bulk_and_savechanges_rollback_discards_both()
    {
        RequireDatabase();
        const int updateId = 9213;
        const int insertItemId = 9214;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = updateId, Key1 = "start" });
            await seed.SaveChangesAsync();
        }

        await using var context = Fixture.CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        context.Items.Add(new Item { Id = insertItemId, Key1 = "new" });
        await context.SaveChangesAsync();

        await context.BulkExecuteAsync(b =>
            b.Update<Item>(op => op.Where(x => x.Id == updateId).Set(x => x.Key1, "discarded")));

        await tx.RollbackAsync();

        await using var verify = Fixture.CreateContext();
        Assert.Equal("start", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == updateId)).Key1);
        Assert.Null(await verify.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == insertItemId));
    }

    [SkippableFact]
    public async Task Bulk_execute_multiple_operations_without_transaction_commits_per_statement()
    {
        RequireDatabase();
        const int idA = 9215;
        const int idB = 9216;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = idA, Key1 = "A" });
            seed.Items.Add(new Item { Id = idB, Key1 = "B" });
            await seed.SaveChangesAsync();
        }

        await using var context = Fixture.CreateContext();
        var result = await context.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == idA).Set(x => x.Key1, "A-upd"));
            b.Update<Item>(op => op.Where(x => x.Id == idB).Set(x => x.Key1, "B-upd"));
        });

        Assert.Equal(2, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        Assert.Equal("A-upd", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idA)).Key1);
        Assert.Equal("B-upd", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idB)).Key1);
    }

    [SkippableFact]
    public async Task Deferred_batch_without_transaction_persists()
    {
        RequireDatabase();
        const int id = 9217;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "before" });
            await seed.SaveChangesAsync();
        }

        await using var context = Fixture.CreateContext();
        var batch = context.CreateBulkBatch();
        batch.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, "batch-after"));
        var result = await batch.ExecuteAsync();

        Assert.Equal(1, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        Assert.Equal("batch-after", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key1);
    }

    [SkippableFact]
    public async Task Deferred_batch_inside_user_transaction_rollback_discards()
    {
        RequireDatabase();
        const int id = 9218;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "before" });
            await seed.SaveChangesAsync();
        }

        await using var context = Fixture.CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        var batch = context.CreateBulkBatch();
        batch.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, "should-discard"));
        await batch.ExecuteAsync();

        await tx.RollbackAsync();

        await using var verify = Fixture.CreateContext();
        Assert.Equal("before", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key1);
    }
}
