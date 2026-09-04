using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.SqlServer;

public class ComputedSetExecutionTests : SqlServerTestBase
{
    public ComputedSetExecutionTests(SqlServerFixture fixture) : base(fixture) { }

    [SkippableFact]
    public async Task Update_computed_multiply_by_factor_persists_in_sql()
    {
        RequireDatabase();
        const string orderNo = "COMP-1001";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Orders.Add(new Order { OrderNo = orderNo, Amount = 100m, Status = OrderStatus.Pending });
            await seed.SaveChangesAsync();
        }

        decimal factor = 1.1m;
        await using (var context = Fixture.CreateContext())
        {
            var result = await context.BulkExecuteAsync(b => b
                .Update<Order>(op => op
                    .Where(x => x.OrderNo == orderNo)
                    .Set(x => x.Amount, x => x.Amount * factor)));

            Assert.Equal(1, result.TotalRowsAffected);
            Assert.Equal(1, result.Operations[0].RowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var reloaded = await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNo);
        Assert.Equal(110m, reloaded.Amount);
    }

    [SkippableFact]
    public async Task Update_computed_captured_variable_is_parameterized()
    {
        RequireDatabase();
        const int id = 10101;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "a", Key2 = 10, Key3 = 0 });
            await seed.SaveChangesAsync();
        }

        int increment = 5;
        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op
                    .Where(x => x.Id == id)
                    .Set(x => x.Key2, x => x.Key2 + increment)));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(15, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);
    }

    [SkippableFact]
    public async Task Update_mixed_constant_and_computed_in_same_operation()
    {
        RequireDatabase();
        const int id = 10102;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "before", Key2 = 10, Key3 = 1 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op
                    .Where(x => x.Id == id)
                    .Set(x => x.Key1, "after")
                    .Set(x => x.Key2, x => x.Key2 * 2)
                    .Set(x => x.Key3, x => x.Key3 + x.Key2)));
        }

        await using var verify = Fixture.CreateContext();
        var item = await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("after", item.Key1);
        Assert.Equal(20, item.Key2); // 10*2
        Assert.Equal(11, item.Key3); // atomic: 1 + original 10, not 1+20
    }

    [SkippableFact]
    public async Task Update_column_to_column_addition()
    {
        RequireDatabase();
        const int id = 10103;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key2 = 7, Key3 = 8 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op
                    .Where(x => x.Id == id)
                    .Set(x => x.Key2, x => x.Key2 + x.Key3)));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(15, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);
    }

    [SkippableFact]
    public async Task Update_negate_and_arith_combination()
    {
        RequireDatabase();
        const int id = 10104;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key2 = 5 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => -x.Key2)));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(-5, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);

        await using (var context2 = Fixture.CreateContext())
        {
            await context2.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => (x.Key2 + 10) * 2)));
        }

        await using var verify2 = Fixture.CreateContext();
        // (-5 +10)*2 =10
        Assert.Equal(10, (await verify2.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);
    }

    [SkippableFact]
    public async Task Update_divide_and_modulo()
    {
        RequireDatabase();
        const int id = 10105;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key2 = 20, Key3 = 6 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => x.Key2 / 2)));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);

        await using (var context2 = Fixture.CreateContext())
        {
            await context2.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key3, x => x.Key3 % 4)));
        }

        await using var verify2 = Fixture.CreateContext();
        Assert.Equal(2, (await verify2.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key3); // 6 %4=2
    }

    [SkippableFact]
    public async Task Update_enum_increment_via_computed()
    {
        RequireDatabase();
        const int id = 10106;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Status = OrderStatus.Pending });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Status, x => x.Status + 1)));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(OrderStatus.Shipped, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Status);
    }

    [SkippableFact]
    public async Task Update_sequential_computed_operations_see_prior_writes()
    {
        RequireDatabase();
        const int id = 10107;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key2 = 10 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => x.Key2 + 10)); // 10->20
                b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => x.Key2 * 2)); // 20->40
            });

            Assert.Equal(2, result.TotalRowsAffected);
            Assert.Equal(1, result.Operations[0].RowsAffected);
            Assert.Equal(1, result.Operations[1].RowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(40, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);
    }

    [SkippableFact]
    public async Task Update_computed_in_single_update_vs_sequential_difference()
    {
        // Proves that a single UPDATE with multiple computed SETs is atomic (all RHS use original row),
        // while two separate operations are sequential (second sees first's write).
        RequireDatabase();
        const int idAtomic = 10108;
        const int idSequential = 10109;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = idAtomic, Key2 = 10, Key3 = 5 });
            seed.Items.Add(new Item { Id = idSequential, Key2 = 10, Key3 = 5 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            // Single UPDATE: SET Key2 = Key2+5, Key3 = Key2+Key3
            // In SQL, Key3 uses original Key2=10, so 10+5=15, not 15+5.
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == idAtomic)
                    .Set(x => x.Key2, x => x.Key2 + 5)
                    .Set(x => x.Key3, x => x.Key2 + x.Key3)));
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == idSequential).Set(x => x.Key2, x => x.Key2 + 5));
                b.Update<Item>(op => op.Where(x => x.Id == idSequential).Set(x => x.Key3, x => x.Key2 + x.Key3));
            });
        }

        await using var verify = Fixture.CreateContext();
        var atomic = await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idAtomic);
        var sequential = await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idSequential);

        Assert.Equal(15, atomic.Key2);
        Assert.Equal(15, atomic.Key3); // 10+5

        Assert.Equal(15, sequential.Key2);
        Assert.Equal(20, sequential.Key3); // 15+5 sequential
    }

    [SkippableFact]
    public async Task Upsert_computed_uses_target_value_not_source()
    {
        RequireDatabase();
        const string orderNo = "COMP-UPS-1";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Orders.Add(new Order { OrderNo = orderNo, Amount = 100m, Status = OrderStatus.Pending });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Orders.BulkUpsertAsync(b => b
                .Add(u => u.On(x => x.OrderNo)
                    .Set(x => x.Amount, x => x.Amount * 1.1m)
                    .Values(new Order { OrderNo = orderNo, Amount = 999m, Status = OrderStatus.Shipped })));

            Assert.Equal(1, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var reloaded = await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNo);
        Assert.Equal(110m, reloaded.Amount); // 100*1.1, not 999*1.1
        // Insert column Status defaults to Shipped from Values, but explicit Set only touches Amount.
        // When Upsert uses explicit Set, only that column is updated on match; other columns stay as before.
        Assert.Equal(OrderStatus.Pending, reloaded.Status);
    }

    [SkippableFact]
    public async Task Upsert_computed_column_to_column()
    {
        RequireDatabase();
        // Re-using Item for int column-to-column test because Order only has one numeric column.
        const int itemId = 10201;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = itemId, Key1 = "ups", Key2 = 10, Key3 = 3 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.Items.BulkUpsertAsync(b => b
                .Add(u => u.On(x => x.Id)
                    .Set(x => x.Key2, x => x.Key2 + x.Key3)
                    .Values(new Item { Id = itemId, Key1 = "ups", Key2 = 999, Key3 = 999 })));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(13, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == itemId)).Key2); // 10+3
    }

    [SkippableFact]
    public async Task Upsert_computed_with_guard()
    {
        RequireDatabase();
        const string code = "COMP-GUARD-1";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Customers.Add(new Customer { Code = code, Name = "Before", Active = true });
            await seed.SaveChangesAsync();
        }

        // Guard blocks protected row, computed would have changed Name if allowed.
        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Customers.BulkUpsertAsync(b => b
                .Add(u => u.On(x => x.Code)
                    .WhenMatched(x => !x.Active)
                    .Set(x => x.Name, x => x.Name + "_suffix")
                    .Values(new Customer { Code = code, Name = "Ignored" })));

            Assert.Equal(0, result.Operations[0].RowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("Before", (await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == code)).Name);

        // Now make it unprotected and ensure computed fires
        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b => b.Update<Customer>(op => op.Where(x => x.Code == code).Set(x => x.Active, false)));
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Customers.BulkUpsertAsync(b => b
                .Add(u => u.On(x => x.Code)
                    .WhenMatched(x => !x.Active)
                    .Set(x => x.Name, x => x.Name + "_suffix")
                    .Values(new Customer { Code = code, Name = "Ignored" })));

            Assert.Equal(1, result.Operations[0].RowsAffected);
        }

        await using var verify2 = Fixture.CreateContext();
        Assert.Equal("Before_suffix", (await verify2.Customers.AsNoTracking().SingleAsync(x => x.Code == code)).Name);
    }

    [SkippableFact]
    public async Task Upsert_insert_path_ignores_computed_on_not_matched()
    {
        RequireDatabase();
        const string orderNo = "COMP-UPS-3";
        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Orders.BulkUpsertAsync(b => b
                .Add(u => u.On(x => x.OrderNo)
                    .Set(x => x.Amount, x => x.Amount * 2m)
                    .Values(new Order { OrderNo = orderNo, Amount = 50m, Status = OrderStatus.Pending })));

            Assert.Equal(1, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var inserted = await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNo);
        Assert.Equal(50m, inserted.Amount); // inserted from VALUES, not doubled
    }

    [SkippableFact]
    public async Task Upsert_mixed_constant_and_computed_with_batching()
    {
        RequireDatabase();
        const string codeExisting = "COMP-MIX-1";
        const string codeNew = "COMP-MIX-2";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Customers.Add(new Customer { Code = codeExisting, Name = "Old", Active = false });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Customers.BulkUpsertAsync(b => b
                .Add(u => u.On(x => x.Code)
                    .Set(x => x.Name, x => x.Name + "_upd")
                    .Set(x => x.Active, true)
                    .Values(new Customer { Code = codeExisting, Name = "Ignored", Active = false })));

            Assert.Equal(1, result.TotalRowsAffected);
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Customers.BulkUpsertAsync(b => b
                .Add(u => u.On(x => x.Code)
                    .Set(x => x.Name, x => x.Name + "_upd")
                    .Values(new[]
                    {
                        new Customer { Code = codeExisting, Name = "Old2", Active = false },
                        new Customer { Code = codeNew, Name = "New", Active = true }
                    })));

            Assert.Equal(2, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("Old_upd_upd", (await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == codeExisting)).Name);
        Assert.Equal("New", (await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == codeNew)).Name);
    }

    [SkippableFact]
    public async Task Computed_with_zero_param_column_to_column_persists()
    {
        RequireDatabase();
        const int id = 10110;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key2 = 3, Key3 = 4 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => x.Key2 + x.Key3)));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(7, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);
    }

    [SkippableFact]
    public async Task Computed_decimal_precision_roundtrip()
    {
        RequireDatabase();
        const string orderNo = "COMP-DEC-1";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Orders.Add(new Order { OrderNo = orderNo, Amount = 123.45m });
            await seed.SaveChangesAsync();
        }

        decimal add = 0.55m;
        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Order>(op => op.Where(x => x.OrderNo == orderNo).Set(x => x.Amount, x => x.Amount + add)));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(124.00m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNo)).Amount);
    }

    [SkippableFact]
    public async Task Multiple_computed_ops_across_tables_in_one_roundtrip()
    {
        RequireDatabase();
        const int itemId = 10210;
        const string orderNo = "COMP-MULTI-1";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = itemId, Key2 = 1 });
            seed.Orders.Add(new Order { OrderNo = orderNo, Amount = 10m });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == itemId).Set(x => x.Key2, x => x.Key2 * 10));
                b.Update<Order>(op => op.Where(x => x.OrderNo == orderNo).Set(x => x.Amount, x => x.Amount + 5m));
                b.Upsert<Customer>(u => u.On(x => x.Code).Values(new Customer { Code = "COMP-MULTI-C", Name = "Hi", Active = true }));
            });

            Assert.Equal(3, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(10, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == itemId)).Key2);
        Assert.Equal(15m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNo)).Amount);
        Assert.NotNull(await verify.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Code == "COMP-MULTI-C"));
    }
}
