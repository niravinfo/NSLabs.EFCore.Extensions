using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Npgsql;

public class NpgsqlUpdateExecutionTests : NpgsqlTestBase
{
    public NpgsqlUpdateExecutionTests(NpgsqlFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Update_by_id_persists_and_reports_per_op_count()
    {
        RequireDatabase();
        const int id = 9101;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key1 = "before", Key2 = 0, Key3 = 0 });
            await ctx.SaveChangesAsync();
        }

        BulkExecuteResult result;
        await using (var ctx = Fixture.CreateContext())
        {
            result = await ctx.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, "after").Set(x => x.Key2, 42)));
        }

        Assert.Equal(1, result.TotalRowsAffected);
        Assert.Single(result.Operations);
        Assert.Equal(1, result.Operations[0].RowsAffected);

        await using var verify = Fixture.CreateContext();
        var item = await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("after", item.Key1);
        Assert.Equal(42, item.Key2);
    }

    [Fact]
    public async Task Mixed_multi_table_batch_applies_everything_in_one_call()
    {
        RequireDatabase();
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            await ctx.Orders.ExecuteDeleteAsync();
            await ctx.AuditLogs.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = 9102, Key1 = "x", Key3 = 1 });
            ctx.Orders.Add(new Order { OrderNo = "ORD-9102", Amount = 5m, Status = OrderStatus.Pending });
            ctx.AuditLogs.Add(new AuditLog { Id = 9103, Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        var result = await ctx2.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 9102).Set(x => x.Key3, 99));
            b.Update<Order>(op => op.Where(x => x.OrderNo == "ORD-9102").Set(x => x.Amount, 55.5m));
            b.Delete<AuditLog>(op => op.Where(x => x.Id == 9103));
        });

        Assert.Equal(3, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        Assert.Equal(99, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == 9102)).Key3);
        Assert.Equal(55.5m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == "ORD-9102")).Amount);
        Assert.False(await verify.AuditLogs.AnyAsync(x => x.Id == 9103));
    }

    [Fact]
    public async Task Per_op_counts_include_zero_match()
    {
        RequireDatabase();
        const int id = 9104;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key1 = "a" });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        var result = await ctx2.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, 7));
            b.Update<Item>(op => op.Where(x => x.Id == 999_999).Set(x => x.Key2, 8));
        });

        Assert.Equal(1, result.Operations[0].RowsAffected);
        Assert.Equal(0, result.Operations[1].RowsAffected);
        Assert.Equal(1, result.TotalRowsAffected);
    }

    [Fact]
    public async Task Sequential_semantics_later_op_sees_earlier_write()
    {
        RequireDatabase();
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = 9201, Key1 = "a", Key2 = 0 });
            ctx.Items.Add(new Item { Id = 9202, Key1 = "a", Key2 = 0 });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 9201).Set(x => x.Key1, "b"));
            b.Update<Item>(op => op.Where(x => x.Key1 == "b").Set(x => x.Key2, 5));
        });

        await using var verify = Fixture.CreateContext();
        var items = await verify.Items.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(5, items[0].Key2);
        Assert.Equal(0, items[1].Key2);
    }

    [Fact]
    public async Task Boolean_enum_datetime_decimal_roundtrip()
    {
        RequireDatabase();
        const int id = 9401;
        var created = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key1 = "t", Active = false, Status = OrderStatus.Pending, CreatedAt = created });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await ctx2.BulkExecuteAsync(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == id)
                .Set(x => x.Active, true)
                .Set(x => x.Status, OrderStatus.Shipped)
                .Set(x => x.CreatedAt, created.AddDays(1))));

        await using var verify = Fixture.CreateContext();
        var item = await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.True(item.Active);
        Assert.Equal(OrderStatus.Shipped, item.Status);
        Assert.Equal(created.AddDays(1), item.CreatedAt);
    }

    [Fact]
    public async Task Composite_entity_row_custom_match_requires_every_term_and_preserves_per_row_counts()
    {
        RequireDatabase();
        const int firstId = 9701;
        const int secondId = 9702;
        var createdAt = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var context = Fixture.CreateContext())
        {
            await context.Items.ExecuteDeleteAsync(cancellationToken);
            context.Items.Add(new Item { Id = firstId, Key1 = "p3-shared", Key2 = 1, Key3 = 10, CreatedAt = createdAt });
            context.Items.Add(new Item { Id = secondId, Key1 = "p3-shared", Key2 = 2, Key3 = 20, CreatedAt = createdAt });
            await context.SaveChangesAsync(cancellationToken);
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
