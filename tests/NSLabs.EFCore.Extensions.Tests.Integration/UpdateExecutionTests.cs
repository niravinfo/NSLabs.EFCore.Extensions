using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration;

public class UpdateExecutionTests : SqlServerTestBase
{
    public UpdateExecutionTests(SqlServerFixture fixture) : base(fixture)
    {
    }

    [SkippableFact]
    public async Task Update_by_id_persists_and_reports_per_op_count()
    {
        RequireDatabase();
        const int id = 9101;

        await using (var context = Fixture.CreateContext())
        {
            context.Items.Add(new Item { Id = id, Key1 = "before", Key2 = 0, Key3 = 0 });
            await context.SaveChangesAsync();
        }

        BulkExecuteResult result;
        await using (var context = Fixture.CreateContext())
        {
            result = await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op
                    .Where(x => x.Id == id)
                    .Set(x => x.Key1, "after")
                    .Set(x => x.Key2, 42)));
        }

        Assert.Equal(1, result.TotalRowsAffected);
        Assert.Single(result.Operations);
        Assert.Equal("Item", result.Operations[0].EntityType);
        Assert.Equal(1, result.Operations[0].RowsAffected);

        await using var verify = Fixture.CreateContext();
        var item = await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("after", item.Key1);
        Assert.Equal(42, item.Key2);
    }

    [SkippableFact]
    public async Task Mixed_multi_table_batch_applies_everything_in_one_call()
    {
        RequireDatabase();

        BulkExecuteResult result;
        await using (var context = Fixture.CreateContext())
        {
            context.Items.Add(new Item { Id = 9102, Key1 = "x", Key3 = 1 });
            context.Orders.Add(new Order { OrderNo = "ORD-9102", Amount = 5m, Status = OrderStatus.Pending });
            context.AuditLogs.Add(new AuditLog { Id = 9103, Created = new DateTime(2026, 1, 1) });
            await context.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            result = await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == 9102).Set(x => x.Key3, 99));
                b.Update<Order>(op => op.Where(x => x.OrderNo == "ORD-9102").Set(x => x.Amount, 55.5m));
                b.Delete<AuditLog>(op => op.Where(x => x.Id == 9103));
            });
        }

        Assert.Equal(3, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        Assert.Equal(99, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == 9102)).Key3);
        Assert.Equal(55.5m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == "ORD-9102")).Amount);
        Assert.False(await verify.AuditLogs.AnyAsync(x => x.Id == 9103));
    }

    [SkippableFact]
    public async Task Per_op_counts_include_zero_match_operations()
    {
        RequireDatabase();
        const int id = 9104;

        await using (var context = Fixture.CreateContext())
        {
            context.Items.Add(new Item { Id = id, Key1 = "a" });
            await context.SaveChangesAsync();
        }

        BulkExecuteResult result;
        await using (var context = Fixture.CreateContext())
        {
            result = await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, 7));
                b.Update<Item>(op => op.Where(x => x.Id == 999_999_999).Set(x => x.Key2, 8));
            });
        }

        Assert.Equal(1, result.Operations[0].RowsAffected);
        Assert.Equal(0, result.Operations[1].RowsAffected);
        Assert.Equal(1, result.TotalRowsAffected);
    }

    [SkippableFact]
    public async Task Throw_if_zero_affected_rolls_back_entire_batch()
    {
        RequireDatabase();
        const int id = 9105;

        await using (var context = Fixture.CreateContext())
        {
            context.Items.Add(new Item { Id = id, Key1 = "keep" });
            await context.SaveChangesAsync();
        }

        await using var context2 = Fixture.CreateContext();
        var exception = await Assert.ThrowsAsync<BulkZeroRowsAffectedException>(() => context2.BulkExecuteAsync(
            b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, "changed"));
                b.Delete<Item>(op => op.Where(x => x.Id == 999_999_999));
            },
            new BulkExecuteOptions { ThrowIfZeroAffected = true }));

        Assert.Equal(1, exception.OperationIndex);

        await using var verify = Fixture.CreateContext();
        Assert.Equal("keep", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key1);
    }

    [SkippableFact]
    public async Task Chunked_batch_across_multiple_commands_stays_atomic()
    {
        RequireDatabase();

        var ids = Enumerable.Range(9111, 5).ToArray();
        await using (var context = Fixture.CreateContext())
        {
            foreach (var id in ids)
            {
                context.Items.Add(new Item { Id = id, Key1 = "orig" });
            }

            await context.SaveChangesAsync();
        }

        var commandTexts = new List<string>();
        var capturedIds = ids.Select(id => (long)id).ToArray();

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b =>
            {
                foreach (var (id, index) in ids.Select((id, i) => (id, i)))
                {
                    b.Update<Item>(op => op
                        .Where(x => x.Id == capturedIds[index])
                        .Set(x => x.Key1, "upd-" + index));
                }
            }, new BulkExecuteOptions
            {
                MaxParametersPerCommand = 4,
                OnCommandText = commandTexts.Add
            });
        }

        Assert.True(commandTexts.Count >= 2, $"Expected chunking into multiple commands but got {commandTexts.Count}.");

        await using var verify = Fixture.CreateContext();
        var items = await verify.Items.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        foreach (var (id, index) in ids.Select((id, i) => (id, i)))
        {
            Assert.Equal("upd-" + index, items[id].Key1);
        }
    }
}
