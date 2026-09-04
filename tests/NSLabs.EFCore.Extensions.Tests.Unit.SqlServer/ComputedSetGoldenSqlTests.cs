using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

public class ComputedSetGoldenSqlTests
{
    [Fact]
    public void Computed_price_multiply_by_factor_on_update()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Order>(op => op
                .Where(x => x.OrderNo == "O-1")
                .Set(x => x.Amount, x => x.Amount * 1.1m)));

        // Amount computed: ([Amount] * @p0)
        Assert.Equal(
            "DECLARE @rc0 int; " +
            "UPDATE [Orders] SET [Amount] = ([Amount] * @p0) WHERE [OrderNo] = @p1; " +
            "SET @rc0 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0;",
            sql);

        var p = Harness.Params(parameters);
        Assert.Equal(1.1m, p["@p0"]);
        Assert.Equal("O-1", p["@p1"]);
    }

    [Fact]
    public void Computed_capture_local_variable()
    {
        decimal factor = 0.9m;
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Order>(op => op
                .Where(x => x.OrderNo == "O-1")
                .Set(x => x.Amount, x => x.Amount * factor)));

        Assert.Contains("SET [Amount] = ([Amount] * @p0)", sql);
        Assert.Equal(factor, Harness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Mixed_constant_and_computed()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => x.Key2 * 2)
                .Set(x => x.Key3, 5)));

        Assert.Equal(
            "DECLARE @rc0 int; " +
            "UPDATE [Items] SET [Key2] = ([Key2] * @p0), [Key3] = @p1 WHERE [Id] = @p2; " +
            "SET @rc0 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0;",
            sql);

        var p = Harness.Params(parameters);
        Assert.Equal(2, p["@p0"]);
        Assert.Equal(5, p["@p1"]);
        Assert.Equal(1, p["@p2"]);
    }

    [Fact]
    public void Mixed_constant_and_computed_reverse_order()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 6)
                .Set(x => x.Key1, "Value1")
                .Set(x => x.Key2, x => x.Key2 + 1)));

        Assert.Contains("SET [Key1] = @p0, [Key2] = ([Key2] + @p1)", sql);
        var p = Harness.Params(parameters);
        Assert.Equal("Value1", p["@p0"]);
        Assert.Equal(1, p["@p1"]);
    }

    [Fact]
    public void Two_updates_one_computed_one_constant_share_counter()
    {
        var (sql, parameters) = Harness.GenerateSingle(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => x.Key2 * 2));
            b.Update<Item>(op => op.Where(x => x.Id == 2).Set(x => x.Key3, 7));
        });

        Assert.Equal(
            "DECLARE @rc0 int; DECLARE @rc1 int; " +
            "UPDATE [Items] SET [Key2] = ([Key2] * @p0) WHERE [Id] = @p1; SET @rc0 = @@ROWCOUNT; " +
            "UPDATE [Items] SET [Key3] = @p2 WHERE [Id] = @p3; SET @rc1 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0, @rc1 AS Op1;",
            sql);

        var p = Harness.Params(parameters);
        Assert.Equal(2, p["@p0"]);
        Assert.Equal(1, p["@p1"]);
        Assert.Equal(7, p["@p2"]);
        Assert.Equal(2, p["@p3"]);
    }

    [Fact]
    public void Column_to_column_addition()
    {
        var (sql, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => x.Key2 + x.Key3)));

        Assert.Contains("SET [Key2] = ([Key2] + [Key3])", sql);
    }

    [Fact]
    public void Negate_and_add()
    {
        var (sql, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => -x.Key2)));

        Assert.Contains("SET [Key2] = -[Key2]", sql);

        var (sql2, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => x.Key2 + 1 - 2)));

        // (([Key2] + @p0) - @p1)
        Assert.Contains("SET [Key2] = (([Key2] + @p0) - @p1)", sql2);
    }

    [Fact]
    public void Complex_expression_with_parentheses()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => (x.Key2 + x.Key3) * 2)));

        // ([Key2]+[Key3]) * 2 -> (([Key2] + [Key3]) * @p0)
        Assert.Contains("SET [Key2] = (([Key2] + [Key3]) * @p0)", sql);
        Assert.Equal(2, Harness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Divide_and_modulo()
    {
        var (sql, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => x.Key2 / 2)));

        Assert.Contains("SET [Key2] = ([Key2] / @p0)", sql);

        var (sql2, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => x.Key2 % 3)));

        Assert.Contains("SET [Key2] = ([Key2] % @p0)", sql2);
    }

    [Fact]
    public void SetProperty_alias_works()
    {
        var (sql, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .SetProperty(x => x.Key2, x => x.Key2 + 1)));

        Assert.Contains("SET [Key2] = ([Key2] + @p0)", sql);
    }

    [Fact]
    public void Computed_with_no_entity_reference_collapses_to_parameter()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => (int)(5 * 2))));

        // No column ref, should be single param with evaluated value 10
        Assert.Contains("SET [Key2] = @p0", sql);
        Assert.Equal(10, Harness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Throws_on_unsupported_method_call()
    {
        var ex = Assert.Throws<NotSupportedException>(() => Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key1, x => x.Key1.PadLeft(5)))));

        Assert.Contains("not supported", ex.Message.ToLower());
    }

    [Fact]
    public void String_trim_is_now_supported()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key1, x => x.Key1.Trim() + "a")));

        Assert.Contains("LTRIM(RTRIM([Key1]))", sql);
        Assert.Contains(" + @p", sql);
        Assert.Equal("a", Harness.Params(parameters).Values.First(v => v is string s && s == "a"));
    }

    [Fact]
    public void Computed_set_on_store_generated_column_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.CreatedAt, x => x.CreatedAt))));

        Assert.Contains("store-generated", ex.Message);
    }

    [Fact]
    public void Duplicate_computed_and_constant_assignments_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key1, "a")
                .Set(x => x.Key1, x => x.Key1 + "b"))));

        Assert.Contains("more than once", ex.Message);
    }

    // Upsert tests

    [Fact]
    public void Upsert_matched_computed_references_target_alias()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Upsert<Order>(u => u
                .On(x => x.OrderNo)
                .Set(x => x.Amount, x => x.Amount * 1.1m)
                .Values(new Order { OrderNo = "O-1", Amount = 100m, Status = OrderStatus.Pending })));

        // Matched update should use [t].[Amount]
        Assert.Contains("WHEN MATCHED THEN UPDATE SET [Amount] = ([t].[Amount] * @p", sql);
        // Insert column list still uses provider values
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", sql);

        var p = Harness.Params(parameters);
        // Amount factor param should be present (last param)
        Assert.Contains(1.1m, p.Values);
    }

    [Fact]
    public void Upsert_matched_computed_with_guard()
    {
        var (sql, _) = Harness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .On(x => x.Code)
                .WhenMatched(x => x.Active)
                .Set(x => x.Name, x => x.Name)
                .Values(new Customer { Code = "A", Name = "X", Active = true })));

        // Guard AND computed assignment with target alias
        Assert.Contains("WHEN MATCHED AND [t].[Active] = 1 THEN UPDATE SET [Name] = [t].[Name]", sql);
    }

    [Fact]
    public void Upsert_mixed_constant_and_computed_chunk_counts_params()
    {
        var chunks = Harness.Generate(
            b => b.Upsert<Customer>(u => u
                .On(x => x.Code)
                .Set(x => x.Name, x => x.Name + "_suffix")
                .Values(new[]
                {
                    new Customer { Code = "A", Name = "X", Active = true },
                    new Customer { Code = "B", Name = "Y", Active = false }
                })),
            new BulkExecuteOptions { MaxParametersPerCommand = 20 });

        Assert.Single(chunks);
        // Computed assignment contributes 1 param (_suffix)
        // plus per-row inserts 3 each = 6 + 1 guard? Actually Name computed has suffix param, plus 6 insert params, plus no guard
        Assert.Contains("([t].[Name] + @p", chunks[0].CommandText);
    }

    [Fact]
    public void Upsert_computed_uses_target_alias_not_source()
    {
        decimal factor = 0.5m;
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Upsert<Order>(u => u
                .On(x => x.OrderNo)
                .Set(x => x.Amount, x => x.Amount * factor)
                .Values(new Order { OrderNo = "O-1", Amount = 10m, Status = OrderStatus.Pending })));

        Assert.Contains("[t].[Amount] * @p", sql);
        Assert.DoesNotContain("[s].[Amount] * @p", sql);
        Assert.Equal(factor, Harness.Params(parameters).Values.First(v => v is decimal d && d == factor));
    }

    [Fact]
    public void Chunking_counts_computed_params_correctly()
    {
        // Computed with 0 params (col+col) should not inflate budget; constant would be 1
        var chunks = Harness.Generate(
            b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => x.Key2 + x.Key3));
                b.Update<Item>(op => op.Where(x => x.Id == 2).Set(x => x.Key2, x => x.Key2 + x.Key3));
            },
            new BulkExecuteOptions { MaxParametersPerCommand = 3 });

        // Each update: 0 assignment params + 1 where param =1 => total 2 per op => 2 ops need 2 chunks with budget 3? 1+2=3 first chunk holds 2 ops? Wait: 1+1=2 <3 so both in 1 chunk? Need to check.
        // With computed 0 params, first chunk can hold both: cost op1=1, op2=1 total 2 fits 3 => 1 chunk
        Assert.Single(chunks);
    }

    [Fact]
    public void Computed_with_captured_enum_converts_correctly()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Status, x => x.Status + 1)));

        // Status enum + int => column + param
        Assert.Contains("SET [Status] = ([Status] + @p0)", sql);
        Assert.Equal(1, Harness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Computed_type_mismatch_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set<object>(x => (object)x.Key1, x => (object)(x.Key2 + 1)))));

        Assert.Contains("not assignable", ex.Message);
    }

    [Fact]
    public void Conditional_expression_is_now_supported()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => x.Key2 > 0 ? x.Key2 + 1 : x.Key2)));

        Assert.Contains("CASE WHEN", sql);
        Assert.Contains("THEN ([Key2] + @p", sql);
        Assert.Contains("ELSE [Key2]", sql);
        Assert.Contains("END", sql);
    }

    [Fact]
    public void Conditional_expression_coalesce_and_string_methods()
    {
        // Coalesce via nullable ParentId -> int Key2
        var (sqlCo, pCo) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => x.ParentId ?? 0)));
        Assert.Contains("COALESCE([ParentId], @p0)", sqlCo);
        Assert.Equal(0, pCo.First().Value);

        // ToUpper
        var (sqlUp, _) = Harness.GenerateSingle(b => b
            .Update<Customer>(op => op.Where(x => x.Code == "A").Set(x => x.Name, x => x.Name.ToUpper())));
        Assert.Contains("UPPER([Name])", sqlUp);

        // ToLower
        var (sqlLow, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.ToLower())));
        Assert.Contains("LOWER([Key1])", sqlLow);

        // Substring + Replace + Concat + Length + Math.Abs
        var (sqlSub, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.Substring(1, 2))));
        Assert.Contains("SUBSTRING([Key1]", sqlSub);

        var (sqlRep, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.Replace("a", "b"))));
        Assert.Contains("REPLACE([Key1]", sqlRep);

        var (sqlConcat, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => string.Concat(x.Key1, "_suf"))));
        Assert.Contains("CONCAT([Key1], @p", sqlConcat);

        var (sqlLen, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => x.Key1.Length)));
        Assert.Contains("LEN([Key1])", sqlLen);

        var (sqlAbs, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => Math.Abs(x.Key2))));
        Assert.Contains("ABS([Key2])", sqlAbs);
    }

    [Fact]
    public void Conditional_with_conditional_in_where_and_upsert_alias()
    {
        var (sql, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1)
                .Set(x => x.Key2, x => x.ParentId == null ? x.Key2 : x.Key2 * 2)));
        Assert.Contains("CASE WHEN [ParentId] IS NULL THEN [Key2] ELSE ([Key2] * @p", sql);

        var (sql2, _) = Harness.GenerateSingle(b => b
            .Upsert<Item>(u => u.On(x => x.Id).Set(x => x.Key2, x => x.ParentId ?? 5).Values(new Item { Id = 1, Key2 = 10 })));
        Assert.Contains("COALESCE([t].[ParentId]", sql2);
    }

    // ---- v2 granular golden tests per PLAN ----
    [Fact]
    public void String_concat_plus()
    {
        var (sql, p) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1 + "_suffix")));
        Assert.Contains("([Key1] + @p0)", sql);
        Assert.Equal("_suffix", p.First().Value);
    }

    [Fact]
    public void String_to_upper_lower_trim()
    {
        var (sqlUp, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.ToUpper())));
        Assert.Contains("UPPER([Key1])", sqlUp);
        var (sqlLow, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.ToLower())));
        Assert.Contains("LOWER([Key1])", sqlLow);
        var (sqlTrim, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.Trim())));
        Assert.Contains("LTRIM(RTRIM([Key1]))", sqlTrim);
        var (sqlLTrim, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.TrimStart())));
        Assert.Contains("LTRIM([Key1])", sqlLTrim);
        var (sqlRTrim, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.TrimEnd())));
        Assert.Contains("RTRIM([Key1])", sqlRTrim);
    }

    [Fact]
    public void String_concat_method()
    {
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => string.Concat(x.Key1, x.Key1))));
        Assert.Contains("CONCAT([Key1], [Key1])", sql);
    }

    [Fact]
    public void Math_abs_round()
    {
        var (sqlAbs, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => Math.Abs(x.Key2))));
        Assert.Contains("ABS([Key2])", sqlAbs);
        var (sqlCeil, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => (int)Math.Ceiling((double)x.Key2))));
        Assert.Contains("CEILING([Key2])", sqlCeil);
        var (sqlFloor, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => (int)Math.Floor((double)x.Key2))));
        Assert.Contains("FLOOR([Key2])", sqlFloor);
        var (sqlRound, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => (int)Math.Round((double)x.Key2))));
        Assert.Contains("ROUND([Key2]", sqlRound);
    }

    [Fact]
    public void Upsert_string_concat_targets_t_alias()
    {
        var (sql, _) = Harness.GenerateSingle(b => b.Upsert<Customer>(u => u.On(x => x.Code).Set(x => x.Name, x => x.Name + "_upd").Values(new Customer { Code = "A", Name = "X" })));
        Assert.Contains("[t].[Name] + @p", sql);
        Assert.DoesNotContain("[s].[Name] + @p", sql);
    }

    [Fact]
    public void Coalesce_golden()
    {
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => x.ParentId ?? -1)));
        Assert.Contains("COALESCE([ParentId], @p", sql);
    }

    [Fact]
    public void Conditional_with_arithmetic()
    {
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => x.Key2 > 0 ? x.Key2 + 10 : x.Key2 - 10)));
        Assert.Contains("CASE WHEN", sql);
        Assert.Contains("[Key2] > @p", sql);
        Assert.Contains("THEN ([Key2] + @p", sql);
        Assert.Contains("ELSE ([Key2] - @p", sql);
    }
}
