using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.Sqlite;

public class SqliteMathMinMaxGoldenSqlTests
{
    [Fact]
    public void Min_column_and_static_emits_min()
    {
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Min(x.Key2, 10))));

        Assert.Contains("SET \"Key2\" = MIN(\"Key2\", @p0)", sql);
        var p = SqliteHarness.Params(parameters);
        Assert.Equal(10, p["@p0"]);
        Assert.Equal(1, p["@p1"]);
    }

    [Fact]
    public void Max_column_and_static_emits_max()
    {
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Max(x.Key2, 10))));

        Assert.Contains("SET \"Key2\" = MAX(\"Key2\", @p0)", sql);
        Assert.Equal(10, SqliteHarness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Min_static_and_column_preserves_arg_order()
    {
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Min(5, x.Key2))));

        Assert.Contains("SET \"Key2\" = MIN(@p0, \"Key2\")", sql);
        Assert.Equal(5, SqliteHarness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Min_column_and_column_has_no_assignment_params()
    {
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Min(x.Key2, x.Key3))));

        Assert.Contains("SET \"Key2\" = MIN(\"Key2\", \"Key3\")", sql);
        Assert.Single(parameters);
        Assert.Equal(1, SqliteHarness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Min_nested_round_with_cap()
    {
        decimal cap = 100m;
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Update<Order>(op => op
                .Where(x => x.OrderNo == "O-1")
                .Set(x => x.Amount, x => Math.Min(Math.Round(x.Amount / cap, 4), cap))));

        Assert.Contains("MIN(ROUND((\"Amount\" / @p0), @p1), @p2)", sql);
        var p = SqliteHarness.Params(parameters);
        Assert.Equal(cap, p["@p0"]);
        Assert.Equal(4, p["@p1"]);
        Assert.Equal(cap, p["@p2"]);
    }

    [Fact]
    public void Min_nested_round_with_column_cap()
    {
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Min((int)Math.Round((double)x.Key2 / x.Key3, 4), x.Key2))));

        Assert.Contains("MIN(ROUND((\"Key2\" / \"Key3\"), @p0), \"Key2\")", sql);
        Assert.Equal(4, SqliteHarness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Min_nested_round_with_literal_cap()
    {
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Update<Order>(op => op
                .Where(x => x.OrderNo == "O-1")
                .Set(x => x.Amount, x => Math.Min(Math.Round(x.Amount / 100m, 4), 9999.99m))));

        Assert.Contains("MIN(ROUND((\"Amount\" / @p0), @p1), @p2)", sql);
        var p = SqliteHarness.Params(parameters);
        Assert.Equal(100m, p["@p0"]);
        Assert.Equal(4, p["@p1"]);
        Assert.Equal(9999.99m, p["@p2"]);
    }

    [Fact]
    public void Max_nested_round_mirror()
    {
        decimal floor = 10m;
        var (sql, _) = SqliteHarness.GenerateSingle(b => b
            .Update<Order>(op => op
                .Where(x => x.OrderNo == "O-1")
                .Set(x => x.Amount, x => Math.Max(Math.Round(x.Amount / floor, 4), floor))));

        Assert.Contains("MAX(ROUND((\"Amount\" / @p0), @p1), @p2)", sql);
    }

    [Fact]
    public void Min_inside_ternary_branches()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => x.Key2 > 0 ? Math.Min(x.Key2, 10) : x.Key2)));

        Assert.Contains("CASE WHEN", sql);
        Assert.Contains("THEN MIN(\"Key2\", @p", sql);
        Assert.Contains("ELSE \"Key2\"", sql);
    }

    [Fact]
    public void Min_static_only_folds_to_parameter()
    {
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Min(3, 5))));

        Assert.Contains("SET \"Key2\" = @p0", sql);
        Assert.DoesNotContain("MIN(", sql);
        Assert.Equal(3, SqliteHarness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Min_max_nesting()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => Math.Max(Math.Min(x.Key2, 10), 1))));

        Assert.Contains("MAX(MIN(\"Key2\", @p0), @p1)", sql);
    }

    [Fact]
    public void MathF_min_emits_min()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.Key2, x => (int)MathF.Min(x.Key2, 5f))));

        Assert.Contains("MIN(\"Key2\", @p", sql);
    }

    [Fact]
    public void Upsert_computed_min_uses_bare_column_not_alias()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b
            .Upsert<Item>(u => u
                .MatchOn(x => x.Id)
                .Update(x => x.Key2, x => Math.Min(x.Key2, 10))
                .Insert(new Item { Id = 1, Key2 = 5 })));

        Assert.Contains("\"Key2\" = MIN(\"Key2\", @p", sql);
        Assert.DoesNotContain("[t].", sql);
    }

}
