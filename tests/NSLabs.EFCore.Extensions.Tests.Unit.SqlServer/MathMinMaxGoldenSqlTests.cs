using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

public class MathMinMaxGoldenSqlTests
{
    [Fact]
    public void Min_column_and_static_emits_least()
    {
        var (sql, parameters) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Min(x.Key2, 10))),
            efCompatibilityLevel: 160);

        Assert.Contains("SET [Key2] = LEAST([Key2], @p0)", sql);
        var p = Harness.Params(parameters);
        Assert.Equal(10, p["@p0"]);
        Assert.Equal(1, p["@p1"]);
    }

    [Fact]
    public void Max_column_and_static_emits_greatest()
    {
        var (sql, parameters) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Max(x.Key2, 10))),
            efCompatibilityLevel: 160);

        Assert.Contains("SET [Key2] = GREATEST([Key2], @p0)", sql);
        Assert.Equal(10, Harness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Min_static_and_column_preserves_arg_order()
    {
        var (sql, parameters) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Min(5, x.Key2))),
            efCompatibilityLevel: 160);

        Assert.Contains("SET [Key2] = LEAST(@p0, [Key2])", sql);
        Assert.Equal(5, Harness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Min_column_and_column_has_no_assignment_params()
    {
        var (sql, parameters) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Min(x.Key2, x.Key3))),
            efCompatibilityLevel: 160);

        Assert.Contains("SET [Key2] = LEAST([Key2], [Key3])", sql);
        // Only the WHERE predicate contributes a parameter.
        Assert.Single(parameters);
        Assert.Equal(1, Harness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Min_nested_round_with_cap()
    {
        decimal cap = 100m;
        var (sql, parameters) = Harness.GenerateSingle(
            b => b.Update<Order>(op => op
                .Where(x => x.OrderNo == "O-1")
                .Set(x => x.Amount, x => Math.Min(Math.Round(x.Amount / cap, 4), cap))),
            efCompatibilityLevel: 160);

        Assert.Contains("LEAST(ROUND(([Amount] / @p0), @p1), @p2)", sql);
        var p = Harness.Params(parameters);
        Assert.Equal(cap, p["@p0"]);
        Assert.Equal(4, p["@p1"]);
        Assert.Equal(cap, p["@p2"]);
    }

    [Fact]
    public void Min_nested_round_with_column_cap()
    {
        var (sql, parameters) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Min((int)Math.Round((double)x.Key2 / x.Key3, 4), x.Key2))),
            efCompatibilityLevel: 160);

        Assert.Contains("LEAST(ROUND(([Key2] / [Key3]), @p0), [Key2])", sql);
        Assert.Equal(4, Harness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Min_nested_round_with_literal_cap()
    {
        var (sql, parameters) = Harness.GenerateSingle(
            b => b.Update<Order>(op => op
                .Where(x => x.OrderNo == "O-1")
                .Set(x => x.Amount, x => Math.Min(Math.Round(x.Amount / 100m, 4), 9999.99m))),
            efCompatibilityLevel: 160);

        Assert.Contains("LEAST(ROUND(([Amount] / @p0), @p1), @p2)", sql);
        var p = Harness.Params(parameters);
        Assert.Equal(100m, p["@p0"]);
        Assert.Equal(4, p["@p1"]);
        Assert.Equal(9999.99m, p["@p2"]);
    }

    [Fact]
    public void Max_nested_round_mirror()
    {
        decimal floor = 10m;
        var (sql, _) = Harness.GenerateSingle(
            b => b.Update<Order>(op => op
                .Where(x => x.OrderNo == "O-1")
                .Set(x => x.Amount, x => Math.Max(Math.Round(x.Amount / floor, 4), floor))),
            efCompatibilityLevel: 160);

        Assert.Contains("GREATEST(ROUND(([Amount] / @p0), @p1), @p2)", sql);
    }

    [Fact]
    public void Min_inside_ternary_branches()
    {
        var (sql, _) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => x.Key2 > 0 ? Math.Min(x.Key2, 10) : x.Key2)),
            efCompatibilityLevel: 160);

        Assert.Contains("CASE WHEN", sql);
        Assert.Contains("THEN LEAST([Key2], @p", sql);
        Assert.Contains("ELSE [Key2]", sql);
    }

    [Fact]
    public void Min_static_only_folds_to_parameter()
    {
        var (sql, parameters) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Min(3, 5))),
            efCompatibilityLevel: 160);

        Assert.Contains("SET [Key2] = @p0", sql);
        Assert.DoesNotContain("LEAST", sql);
        Assert.Equal(3, Harness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Min_max_nesting()
    {
        var (sql, _) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Max(Math.Min(x.Key2, 10), 1))),
            efCompatibilityLevel: 160);

        Assert.Contains("GREATEST(LEAST([Key2], @p0), @p1)", sql);
    }

    [Fact]
    public void MathF_min_emits_least()
    {
        var (sql, _) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => (int)MathF.Min(x.Key2, 5f))),
            efCompatibilityLevel: 160);

        Assert.Contains("LEAST([Key2], @p", sql);
    }

    [Fact]
    public void Upsert_computed_min_uses_target_alias()
    {
        var (sql, _) = Harness.GenerateSingle(
            b => b.Upsert<Order>(u => u
                .MatchOn(x => x.OrderNo)
                .Update(x => x.Amount, x => Math.Max(x.Amount, 100m))
                .Insert(new Order { OrderNo = "O-1", Amount = 10m, Status = OrderStatus.Pending })),
            efCompatibilityLevel: 160);

        Assert.Contains("GREATEST([t].[Amount], @p", sql);
        Assert.DoesNotContain("GREATEST([s].[Amount]", sql);
    }

    [Fact]
    public void Default_compat_throws_with_actionable_message()
    {
        // EF Core default is 150 — below the 160 required for LEAST.
        var ex = Assert.Throws<NotSupportedException>(() => Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Min(x.Key2, 10)))));

        Assert.Contains("160", ex.Message);
        Assert.Contains("UseCompatibilityLevel", ex.Message);
        Assert.Contains("COMPATIBILITY_LEVEL", ex.Message);
        Assert.Contains("LEAST", ex.Message);
    }

    [Fact]
    public void Compat_159_throws_but_160_and_170_emit()
    {
        Assert.Throws<NotSupportedException>(() => Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => Math.Max(x.Key2, 1))),
            efCompatibilityLevel: 159));

        var (sql160, _) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => Math.Max(x.Key2, 1))),
            efCompatibilityLevel: 160);
        Assert.Contains("GREATEST([Key2], @p0)", sql160);

        var (sql170, _) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => Math.Min(x.Key2, 1))),
            efCompatibilityLevel: 170);
        Assert.Contains("LEAST([Key2], @p0)", sql170);
    }

    [Fact]
    public void Chunking_counts_min_params_correctly()
    {
        var chunks = Harness.Generate(
            b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => Math.Min(x.Key2, 10)));
                b.Update<Item>(op => op.Where(x => x.Id == 2).Set(x => x.Key2, x => Math.Max(x.Key2, 20)));
            },
            new BulkExecuteOptions { MaxParametersPerCommand = 3 },
            efCompatibilityLevel: 160);

        // Each op costs 2 params (1 assignment + 1 predicate); budget 3 fits one op per chunk.
        Assert.Equal(2, chunks.Count);
    }
}
