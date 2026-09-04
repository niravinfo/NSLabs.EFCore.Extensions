using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.SqlServer;

/// <summary>
/// Integration tests for v2 computed SET features:
/// string concat (+), Coalesce (??), Conditional (? :), string methods (ToUpper, ToLower, Trim, Substring, Replace, Concat, Length), Math (Abs, Ceiling, Floor, Round).
/// Golden-SQL tests in ComputedSetGoldenSqlTests.cs verify emission; these tests verify actual SQL execution via Testcontainers SQL Server.
/// </summary>
public class ComputedSetV2ExecutionTests : SqlServerTestBase
{
    public ComputedSetV2ExecutionTests(SqlServerFixture fixture) : base(fixture) { }

    [SkippableFact]
    public async Task Update_string_concat_plus_persists()
    {
        RequireDatabase();
        const int id = 20101;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "Base", Key2 = 1 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, x => x.Key1 + "_suffix")));
            Assert.Equal(1, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("Base_suffix", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key1);
    }

    [SkippableFact]
    public async Task Update_string_to_upper_and_to_lower_persists()
    {
        RequireDatabase();
        const int idUp = 20102;
        const int idLow = 20103;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = idUp, Key1 = "hello" });
            seed.Items.Add(new Item { Id = idLow, Key1 = "HELLO" });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == idUp).Set(x => x.Key1, x => x.Key1.ToUpper()));
                b.Update<Item>(op => op.Where(x => x.Id == idLow).Set(x => x.Key1, x => x.Key1.ToLower()));
            });
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("HELLO", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idUp)).Key1);
        Assert.Equal("hello", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idLow)).Key1);
    }

    [SkippableFact]
    public async Task Update_string_trim_variants_persists()
    {
        RequireDatabase();
        const int idTrim = 20104;
        const int idLTrim = 20105;
        const int idRTrim = 20106;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = idTrim, Key1 = "  hello  " });
            seed.Items.Add(new Item { Id = idLTrim, Key1 = "  hello  " });
            seed.Items.Add(new Item { Id = idRTrim, Key1 = "  hello  " });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == idTrim).Set(x => x.Key1, x => x.Key1.Trim()));
                b.Update<Item>(op => op.Where(x => x.Id == idLTrim).Set(x => x.Key1, x => x.Key1.TrimStart()));
                b.Update<Item>(op => op.Where(x => x.Id == idRTrim).Set(x => x.Key1, x => x.Key1.TrimEnd()));
            });
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("hello", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idTrim)).Key1);
        Assert.Equal("hello  ", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idLTrim)).Key1);
        Assert.Equal("  hello", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idRTrim)).Key1);
    }

    [SkippableFact]
    public async Task Update_string_substring_persists()
    {
        RequireDatabase();
        const int id = 20107;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "BaseSuffix" });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, x => x.Key1.Substring(0, 4))));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("Base", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key1);

        // Substring with variable start (captured) -> tests AddOne with param
        const int id2 = 20108;
        await using (var seed2 = Fixture.CreateContext())
        {
            seed2.Items.Add(new Item { Id = id2, Key1 = "BaseSuffix" });
            await seed2.SaveChangesAsync();
        }
        int start = 4;
        await using (var context2 = Fixture.CreateContext())
        {
            await context2.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id2).Set(x => x.Key1, x => x.Key1.Substring(start, 6))));
        }
        await using var verify2 = Fixture.CreateContext();
        Assert.Equal("Suffix", (await verify2.Items.AsNoTracking().SingleAsync(x => x.Id == id2)).Key1);
    }

    [SkippableFact]
    public async Task Update_string_replace_persists()
    {
        RequireDatabase();
        const int id = 20109;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key1 = "a-a-a" });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, x => x.Key1.Replace("a", "b"))));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("b-b-b", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key1);
    }

    [SkippableFact]
    public async Task Update_string_concat_method_and_length_persists()
    {
        RequireDatabase();
        const int idConcat = 20110;
        const int idLen = 20111;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = idConcat, Key1 = "Base" });
            seed.Items.Add(new Item { Id = idLen, Key1 = "Hello", Key2 = 0 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == idConcat).Set(x => x.Key1, x => string.Concat(x.Key1, "_c")));
                b.Update<Item>(op => op.Where(x => x.Id == idLen).Set(x => x.Key2, x => x.Key1.Length));
            });
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("Base_c", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idConcat)).Key1);
        Assert.Equal(5, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idLen)).Key2);
    }

    [SkippableFact]
    public async Task Update_coalesce_persists()
    {
        RequireDatabase();
        const int idNull = 20112;
        const int idNotNull = 20113;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = idNull, Key2 = 0, ParentId = null });
            seed.Items.Add(new Item { Id = idNotNull, Key2 = 0, ParentId = 7 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == idNull).Set(x => x.Key2, x => x.ParentId ?? 5));
                b.Update<Item>(op => op.Where(x => x.Id == idNotNull).Set(x => x.Key2, x => x.ParentId ?? 5));
            });
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(5, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idNull)).Key2);
        Assert.Equal(7, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idNotNull)).Key2);
    }

    [SkippableFact]
    public async Task Update_conditional_case_when_simple()
    {
        RequireDatabase();
        const int idLow = 20114;
        const int idHigh = 20115;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = idLow, Key2 = 5 });
            seed.Items.Add(new Item { Id = idHigh, Key2 = 20 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == idLow).Set(x => x.Key2, x => x.Key2 > 10 ? x.Key2 + 100 : x.Key2 + 1));
                b.Update<Item>(op => op.Where(x => x.Id == idHigh).Set(x => x.Key2, x => x.Key2 > 10 ? x.Key2 + 100 : x.Key2 + 1));
            });
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(6, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idLow)).Key2);
        Assert.Equal(120, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idHigh)).Key2);
    }

    [SkippableFact]
    public async Task Update_conditional_with_is_null_and_coalesce()
    {
        RequireDatabase();
        const int idNull = 20116;
        const int idNotNull = 20117;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = idNull, Key2 = 10, ParentId = null });
            seed.Items.Add(new Item { Id = idNotNull, Key2 = 10, ParentId = 5 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == idNull).Set(x => x.Key2, x => x.ParentId == null ? x.Key2 * 2 : x.Key2));
                b.Update<Item>(op => op.Where(x => x.Id == idNotNull).Set(x => x.Key2, x => x.ParentId == null ? x.Key2 * 2 : x.Key2));
            });
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(20, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idNull)).Key2);
        Assert.Equal(10, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idNotNull)).Key2);
    }

    [SkippableFact]
    public async Task Update_conditional_with_and_or_not_and_boolean_column()
    {
        RequireDatabase();
        const int id1 = 20118; // Active true, Key2 20 -> true && Key2>10 => true -> +1
        const int id2 = 20119; // Active false, Key2 20 -> false && ... => false -> -1
        const int id3 = 20120; // Active false, Key2 5 -> !Active true -> +10
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id1, Active = true, Key2 = 20 });
            seed.Items.Add(new Item { Id = id2, Active = false, Key2 = 20 });
            seed.Items.Add(new Item { Id = id3, Active = false, Key2 = 5 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == id1).Set(x => x.Key2, x => x.Active && x.Key2 > 10 ? x.Key2 + 1 : x.Key2 - 1));
                b.Update<Item>(op => op.Where(x => x.Id == id2).Set(x => x.Key2, x => x.Active && x.Key2 > 10 ? x.Key2 + 1 : x.Key2 - 1));
                b.Update<Item>(op => op.Where(x => x.Id == id3).Set(x => x.Key2, x => !x.Active ? x.Key2 + 10 : x.Key2));
            });
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(21, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id1)).Key2);
        Assert.Equal(19, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id2)).Key2);
        Assert.Equal(15, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id3)).Key2);
    }

    [SkippableFact]
    public async Task Update_conditional_string_branches()
    {
        RequireDatabase();
        const int idActive = 20121;
        const int idInactive = 20122;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = idActive, Key1 = "Base", Active = true });
            seed.Items.Add(new Item { Id = idInactive, Key1 = "Base", Active = false });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == idActive).Set(x => x.Key1, x => x.Active ? x.Key1 + "_a" : x.Key1 + "_b"));
                b.Update<Item>(op => op.Where(x => x.Id == idInactive).Set(x => x.Key1, x => x.Active ? x.Key1 + "_a" : x.Key1 + "_b"));
            });
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("Base_a", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idActive)).Key1);
        Assert.Equal("Base_b", (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idInactive)).Key1);
    }

    [SkippableFact]
    public async Task Update_conditional_with_arithmetic_in_branches()
    {
        RequireDatabase();
        const int id = 20123;
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = id, Key2 = 10, Key3 = 3 });
            await seed.SaveChangesAsync();
        }

        decimal factor = 0.5m;
        await using (var context = Fixture.CreateContext())
        {
            // Test branch contains arithmetic with captured param
            await context.BulkExecuteAsync(b => b
                .Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key2, x => x.ParentId == null ? x.Key2 + x.Key3 : (int)(x.Key2 * factor))));
            // ParentId is null -> branch (10+3)=13
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(13, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Key2);
    }

    [SkippableFact]
    public async Task Update_math_functions_persists()
    {
        RequireDatabase();
        const int idAbs = 20124;
        const string orderNoCeil = "COMP-MATH-1";
        const string orderNoFloor = "COMP-MATH-2";
        const string orderNoRound = "COMP-MATH-3";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = idAbs, Key2 = -5 });
            seed.Orders.Add(new Order { OrderNo = orderNoCeil, Amount = 10.2m });
            seed.Orders.Add(new Order { OrderNo = orderNoFloor, Amount = 10.8m });
            seed.Orders.Add(new Order { OrderNo = orderNoRound, Amount = 10.5m });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == idAbs).Set(x => x.Key2, x => Math.Abs(x.Key2)));
                b.Update<Order>(op => op.Where(x => x.OrderNo == orderNoCeil).Set(x => x.Amount, x => Math.Ceiling(x.Amount)));
                b.Update<Order>(op => op.Where(x => x.OrderNo == orderNoFloor).Set(x => x.Amount, x => Math.Floor(x.Amount)));
                b.Update<Order>(op => op.Where(x => x.OrderNo == orderNoRound).Set(x => x.Amount, x => Math.Round(x.Amount)));
            });
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(5, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == idAbs)).Key2);
        Assert.Equal(11m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNoCeil)).Amount);
        Assert.Equal(10m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNoFloor)).Amount);
        Assert.Equal(11m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNoRound)).Amount);
    }

    [SkippableFact]
    public async Task Upsert_string_concat_and_coalesce_uses_target_alias()
    {
        RequireDatabase();
        const string codeExisting = "COMP-V2-UPS-1";
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
                    .Values(new Customer { Code = codeExisting, Name = "Ignored" })));
            Assert.Equal(1, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("Old_upd", (await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == codeExisting)).Name);

        // Coalesce on upsert matched path uses t alias; not-matched inserts raw Values
        const int itemIdExisting = 20125;
        const int itemIdNew = 20126;
        await using (var seed2 = Fixture.CreateContext())
        {
            seed2.Items.Add(new Item { Id = itemIdExisting, Key2 = 5, ParentId = null });
            await seed2.SaveChangesAsync();
        }

        await using (var context2 = Fixture.CreateContext())
        {
            var result = await context2.Items.BulkUpsertAsync(b => b
                .Add(u => u.On(x => x.Id)
                    .Set(x => x.Key2, x => x.ParentId ?? 99)
                    .Values(new Item { Id = itemIdExisting, Key2 = 999, ParentId = 123 })));
            Assert.Equal(1, result.TotalRowsAffected);
        }

        await using var verify2 = Fixture.CreateContext();
        // Existing row ParentId is null -> coalesce 99
        Assert.Equal(99, (await verify2.Items.AsNoTracking().SingleAsync(x => x.Id == itemIdExisting)).Key2);

        // Not-matched inserts via Values, not via computed coalesce
        await using (var context3 = Fixture.CreateContext())
        {
            var result = await context3.Items.BulkUpsertAsync(b => b
                .Add(u => u.On(x => x.Id)
                    .Set(x => x.Key2, x => x.ParentId ?? 99)
                    .Values(new Item { Id = itemIdNew, Key2 = 77, ParentId = 55 })));
            Assert.Equal(1, result.TotalRowsAffected);
        }

        await using var verify3 = Fixture.CreateContext();
        Assert.Equal(77, (await verify3.Items.AsNoTracking().SingleAsync(x => x.Id == itemIdNew)).Key2);
        Assert.Equal(55, (await verify3.Items.AsNoTracking().SingleAsync(x => x.Id == itemIdNew)).ParentId);
    }

    [SkippableFact]
    public async Task Upsert_conditional_case_when()
    {
        RequireDatabase();
        const string code = "COMP-V2-COND-UPS-1";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Customers.Add(new Customer { Code = code, Name = "Base", Active = true });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            await context.Customers.BulkUpsertAsync(b => b
                .Add(u => u.On(x => x.Code)
                    .Set(x => x.Name, x => x.Active ? x.Name + "_a" : x.Name + "_b")
                    .Values(new Customer { Code = code, Name = "Ignored" })));
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal("Base_a", (await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == code)).Name);
    }

    [SkippableFact]
    public async Task Combined_batch_with_string_conditional_coalesce_and_math()
    {
        RequireDatabase();
        const int itemId = 20127;
        const string orderNo = "COMP-V2-MULTI-1";
        const string custCode = "COMP-V2-MULTI-C";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = itemId, Key1 = "mix", Key2 = -10, ParentId = null });
            seed.Orders.Add(new Order { OrderNo = orderNo, Amount = 10.2m });
            seed.Customers.Add(new Customer { Code = custCode, Name = "MiX", Active = false });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == itemId)
                    .Set(x => x.Key1, x => x.Key1.ToUpper() + "_X")
                    .Set(x => x.Key2, x => x.ParentId ?? Math.Abs(x.Key2))
                    .Set(x => x.Key3, x => x.Key2 > 0 ? x.Key2 + 1 : 0));
                b.Update<Order>(op => op.Where(x => x.OrderNo == orderNo).Set(x => x.Amount, x => Math.Ceiling(x.Amount)));
                b.Update<Customer>(op => op.Where(x => x.Code == custCode).Set(x => x.Name, x => x.Active ? x.Name : x.Name.ToLower()));
            });
            Assert.Equal(3, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var item = await verify.Items.AsNoTracking().SingleAsync(x => x.Id == itemId);
        Assert.Equal("MIX_X", item.Key1);
        Assert.Equal(10, item.Key2); // ParentId null -> Abs(-10)=10
        Assert.Equal(0, item.Key3); // Key2 original -10 >0? false -> 0 (atomic, original -10)
        Assert.Equal(11m, (await verify.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == orderNo)).Amount);
        Assert.Equal("mix", (await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == custCode)).Name);
    }
}
