using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.SqlServer;

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
    public async Task Throw_if_zero_affected_without_transaction_throws_but_does_not_roll_back_previous_ops()
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
        // Without an ambient transaction each statement commits individually;
        // the first update is not rolled back when the second operation throws.
        Assert.Equal("changed", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key1);
    }

    [SkippableFact]
    public async Task Throw_if_zero_affected_rolls_back_when_wrapped_in_transaction()
    {
        RequireDatabase();
        const int id = 9106;

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "keep" });
            await seed.SaveChangesAsync();
        }

        await using var context = Fixture.CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        var exception = await Assert.ThrowsAsync<BulkZeroRowsAffectedException>(() => context.BulkExecuteAsync(
            b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, "changed"));
                b.Delete<Item>(op => op.Where(x => x.Id == 999_999_999));
            },
            new BulkExecuteOptions { ThrowIfZeroAffected = true }));

        Assert.Equal(1, exception.OperationIndex);
        await tx.RollbackAsync();

        await using var verify = Fixture.CreateContext();
        Assert.Equal("keep", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key1);
    }

    [SkippableFact]
    public async Task Same_table_updates_with_different_filters_apply_independently()
    {
        RequireDatabase();

        await using (var seed = Fixture.CreateContext())
        {
            // Shared database across tests; clear stray Items so row counts are exact.
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 9801, Key1 = "alpha", Key2 = 0, Key3 = 0, Status = OrderStatus.Pending, Active = false });
            seed.Items.Add(new Item { Id = 9802, Key1 = "beta", Key2 = 0, Key3 = 0, Status = OrderStatus.Pending, Active = true });
            seed.Items.Add(new Item { Id = 9803, Key1 = "gamma", Key2 = 0, Key3 = 0, Status = OrderStatus.Shipped, Active = false });
            await seed.SaveChangesAsync();
        }

        BulkExecuteResult result;
        await using (var context = Fixture.CreateContext())
        {
            // Three operations against Items, each with its own predicate shape and its own
            // partial column payload, executed sequentially in user order.
            result = await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Key1 == "alpha").Set(x => x.Key3, 11));
                b.Update<Item>(op => op.Where(x => x.Status == OrderStatus.Shipped).Set(x => x.Key2, 22));
                b.Update<Item>(op => op.Where(x => !x.Active).Set(x => x.Key1, "delta"));
            });
        }

        Assert.Equal(3, result.Operations.Count);
        Assert.Equal(1, result.Operations[0].RowsAffected);
        Assert.Equal(1, result.Operations[1].RowsAffected);
        Assert.Equal(2, result.Operations[2].RowsAffected);
        Assert.Equal(4, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        var items = await verify.Items.AsNoTracking().Where(x => x.Id >= 9801 && x.Id <= 9803).ToDictionaryAsync(x => x.Id);

        // 9801: matched op #0 (Key3) and op #2 (Key1).
        Assert.Equal(11, items[9801].Key3);
        Assert.Equal("delta", items[9801].Key1);
        Assert.Equal(0, items[9801].Key2);

        // 9802: active and pending, matched nothing.
        Assert.Equal("beta", items[9802].Key1);
        Assert.Equal(0, items[9802].Key2);
        Assert.Equal(0, items[9802].Key3);

        // 9803: matched op #1 (shipped), then op #2 (inactive).
        Assert.Equal(22, items[9803].Key2);
        Assert.Equal("delta", items[9803].Key1);
        Assert.Equal(0, items[9803].Key3);
    }

    [SkippableFact]
    public async Task Set_assignments_can_be_added_dynamically_with_control_flow()
    {
        RequireDatabase();

        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 9901, Key1 = "before", Key2 = 0, Key3 = 0 });
            await seed.SaveChangesAsync();
        }

        var applyRename = true;
        var applySkipColumn = false;
        var increments = new[] { 5, 10 };

        BulkExecuteResult result;
        await using (var context = Fixture.CreateContext())
        {
            // The configure delegate is ordinary C#: if/else and foreach decide which
            // Set(...) calls run; values are captured at call time.
            result = await context.BulkExecuteAsync(b => b
                .Update<Item>(op =>
                {
                    op.Where(x => x.Id == 9901);

                    if (applyRename)
                    {
                        op.Set(x => x.Key1, "after");
                    }

                    if (applySkipColumn)
                    {
                        op.Set(x => x.Key3, 999);
                    }

                    // A column may only be assigned once per operation, so aggregate
                    // inside the loop and issue a single Set(...) per column.
                    var total = 0;
                    foreach (var increment in increments)
                    {
                        total += increment;
                    }

                    op.Set(x => x.Key2, total);
                }));
        }

        Assert.Equal(1, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();
        var item = await verify.Items.AsNoTracking().SingleAsync(x => x.Id == 9901);
        Assert.Equal("after", item.Key1);
        Assert.Equal(15, item.Key2);
        Assert.Equal(0, item.Key3);

        // Duplicate Set(...) on the same column fails fast with a clear error instead of
        // a SQL Server "column specified more than once" exception at execution time.
        await using var assertContext = Fixture.CreateContext();
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => assertContext.BulkExecuteAsync(b => b
            .Update<Item>(op =>
            {
                op.Where(x => x.Id == 9901);
                op.Set(x => x.Key1, "first");
                op.Set(x => x.Key1, "second");
            })));

        Assert.Contains("'Key1' more than once", duplicate.Message);
    }

    [SkippableFact]
    public async Task Multiple_entity_types_update_in_one_batch_with_own_filters_and_sets()
    {
        RequireDatabase();

        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 9911, Key1 = "multi-a", Key2 = 0, Key3 = 0, Active = false });
            seed.Orders.Add(new Order { OrderNo = "ORD-9912", Amount = 10m, Status = OrderStatus.Pending });
            seed.Customers.Add(new Customer { Code = "C-9913", Name = "Before", Active = true });
            await seed.SaveChangesAsync();
        }

        // One batch, three different entity types: each operation carries its own
        // Where(...) predicate and its own Set(...) payload for its table.
        BulkExecuteResult result;
        await using (var context = Fixture.CreateContext())
        {
            result = await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op
                    .Where(x => x.Key1 == "multi-a")
                    .Set(x => x.Key2, 100)
                    .Set(x => x.Key3, 200));

                b.Update<Order>(op => op
                    .Where(x => x.OrderNo == "ORD-9912" && x.Status == OrderStatus.Pending)
                    .Set(x => x.Amount, 99.5m)
                    .Set(x => x.Status, OrderStatus.Shipped));

                b.Update<Customer>(op => op
                    .Where(x => x.Code == "C-9913")
                    .Set(x => x.Active, false));
            });
        }

        Assert.Equal(3, result.Operations.Count);
        Assert.Equal("Item", result.Operations[0].EntityType);
        Assert.Equal("Order", result.Operations[1].EntityType);
        Assert.Equal("Customer", result.Operations[2].EntityType);
        Assert.All(result.Operations, op => Assert.Equal(1, op.RowsAffected));
        Assert.Equal(3, result.TotalRowsAffected);

        await using var verify = Fixture.CreateContext();

        var item = await verify.Items.AsNoTracking().SingleAsync(x => x.Id == 9911);
        Assert.Equal(100, item.Key2);
        Assert.Equal(200, item.Key3);

        var order = await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == "ORD-9912");
        Assert.Equal(99.5m, order.Amount);
        Assert.Equal(OrderStatus.Shipped, order.Status);

        var customer = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == "C-9913");
        Assert.False(customer.Active);
        Assert.Equal("Before", customer.Name);
    }

    [SkippableFact]
    public async Task Same_entity_operations_with_dynamically_built_filters_and_sets()
    {
        RequireDatabase();

        await using (var seed = Fixture.CreateContext())
        {
            await seed.Items.ExecuteDeleteAsync();
            seed.Items.Add(new Item { Id = 9921, Key1 = "dyn-a", Key2 = 0, Key3 = 0, Active = false });
            seed.Items.Add(new Item { Id = 9922, Key1 = "dyn-b", Key2 = 0, Key3 = 0, Active = true });
            seed.Items.Add(new Item { Id = 9923, Key1 = "dyn-c", Key2 = 0, Key3 = 0, Active = true });
            await seed.SaveChangesAsync();
        }

        // Simulates runtime-driven patching: each entry describes an operation whose
        // predicate shape and SET payload are only known when the loop runs.
        var patches = new[]
        {
            (MatchKey1: "dyn-a", RequireActive: false, Mode: "rename"),
            (MatchKey1: "dyn-b", RequireActive: true, Mode: "count"),
            (MatchKey1: "dyn-c", RequireActive: true, Mode: "flag")
        };

        BulkExecuteResult result;
        await using (var context = Fixture.CreateContext())
        {
            result = await context.BulkExecuteAsync(b =>
            {
                foreach (var patch in patches)
                {
                    b.Update<Item>(op =>
                    {
                        // Dynamic WHERE: the predicate shape depends on the patch.
                        if (patch.RequireActive)
                        {
                            op.Where(x => x.Key1 == patch.MatchKey1 && x.Active);
                        }
                        else
                        {
                            op.Where(x => x.Key1 == patch.MatchKey1);
                        }

                        // Dynamic SET: which columns get assigned depends on the mode.
                        switch (patch.Mode)
                        {
                            case "rename":
                                op.Set(x => x.Key1, "renamed-" + patch.MatchKey1);
                                break;
                            case "count":
                                op.Set(x => x.Key2, 42);
                                break;
                            default:
                                op.Set(x => x.Key3, 777);
                                break;
                        }
                    });
                }
            });
        }

        Assert.Equal(3, result.Operations.Count);
        Assert.All(result.Operations, op => Assert.Equal(1, op.RowsAffected));

        await using var verify = Fixture.CreateContext();
        var items = await verify.Items.AsNoTracking().Where(x => x.Id >= 9921 && x.Id <= 9923).ToDictionaryAsync(x => x.Id);

        Assert.Equal("renamed-dyn-a", items[9921].Key1);
        Assert.Equal(0, items[9921].Key3);

        Assert.Equal("dyn-b", items[9922].Key1);
        Assert.Equal(42, items[9922].Key2);
        Assert.Equal(0, items[9922].Key3);

        Assert.Equal(777, items[9923].Key3);
        Assert.Equal(0, items[9923].Key2);
    }

    [SkippableFact]
    public async Task Chunked_batch_across_multiple_commands_executes_all_chunks_without_transaction()
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

    [SkippableFact]
    public async Task Chunked_batch_inside_user_transaction_is_atomic()
    {
        RequireDatabase();

        var ids = Enumerable.Range(9121, 5).ToArray();
        await using (var seed = Fixture.CreateContext())
        {
            foreach (var id in ids)
            {
                seed.Items.Add(new Item { Id = id, Key1 = "orig" });
            }

            await seed.SaveChangesAsync();
        }

        var commandTexts = new List<string>();
        var capturedIds = ids.Select(id => (long)id).ToArray();

        await using var context = Fixture.CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        await context.BulkExecuteAsync(b =>
        {
            foreach (var (id, index) in ids.Select((id, i) => (id, i)))
            {
                b.Update<Item>(op => op
                    .Where(x => x.Id == capturedIds[index])
                    .Set(x => x.Key1, "tx-upd-" + index));
            }
        }, new BulkExecuteOptions
        {
            MaxParametersPerCommand = 4,
            OnCommandText = commandTexts.Add
        });

        await tx.CommitAsync();

        Assert.True(commandTexts.Count >= 2, $"Expected chunking into multiple commands but got {commandTexts.Count}.");

        await using var verify = Fixture.CreateContext();
        var items = await verify.Items.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        foreach (var (id, index) in ids.Select((id, i) => (id, i)))
        {
            Assert.Equal("tx-upd-" + index, items[id].Key1);
        }
    }
}
