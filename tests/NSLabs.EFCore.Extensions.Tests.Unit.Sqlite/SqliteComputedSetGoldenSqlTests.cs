using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.Sqlite;

public class SqliteComputedSetGoldenSqlTests
{
    [Fact]
    public void Computed_price_multiply_uses_sqlite_quoting()
    {
        var (sql, p) = SqliteHarness.GenerateSingle(b => b
            .Update<Order>(op => op.Where(x => x.OrderNo == "O-1").Set(x => x.Amount, x => x.Amount * 1.1m)));

        Assert.Contains("UPDATE \"Orders\" SET \"Amount\" = (\"Amount\" * @p0) WHERE \"OrderNo\" = @p1;", sql);
        Assert.Equal(1.1m, SqliteHarness.Params(p)["@p0"]);
    }

    [Fact]
    public void String_concat_plus_uses_double_pipe()
    {
        var (sql, p) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1 + "_suffix")));

        Assert.Contains("(\"Key1\" || @p0)", sql);
        Assert.DoesNotContain(" + @p", sql);
        Assert.Equal("_suffix", p.First().Value);
    }

    [Fact]
    public void String_functions_map_correctly()
    {
        var (sqlUp, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.ToUpper())));
        Assert.Contains("UPPER(\"Key1\")", sqlUp);

        var (sqlLow, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.ToLower())));
        Assert.Contains("LOWER(\"Key1\")", sqlLow);

        var (sqlTrim, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.Trim())));
        Assert.Contains("TRIM(\"Key1\")", sqlTrim);
        Assert.DoesNotContain("LTRIM(RTRIM", sqlTrim);

        var (sqlLen, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => x.Key1.Length)));
        Assert.Contains("LENGTH(\"Key1\")", sqlLen);
        Assert.DoesNotContain("LEN(", sqlLen);
    }

    [Fact]
    public void Substring_maps_to_substr()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => x.Key1.Substring(1, 2))));
        Assert.Contains("SUBSTR(\"Key1\"", sql);
        Assert.DoesNotContain("SUBSTRING", sql);
    }

    [Fact]
    public void Concat_maps_to_double_pipe()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, x => string.Concat(x.Key1, "_suf"))));
        Assert.Contains("||", sql);
        Assert.DoesNotContain("CONCAT(", sql);
    }

    [Fact]
    public void Math_functions_map_correctly()
    {
        var (sqlAbs, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => Math.Abs(x.Key2))));
        Assert.Contains("ABS(\"Key2\")", sqlAbs);

        var (sqlCeil, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => (int)Math.Ceiling((double)x.Key2))));
        Assert.Contains("CEIL(\"Key2\")", sqlCeil);

        var (sqlFloor, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => (int)Math.Floor((double)x.Key2))));
        Assert.Contains("FLOOR(\"Key2\")", sqlFloor);

        var (sqlTrunc, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => (int)Math.Truncate((double)x.Key2))));
        Assert.Contains("CAST(\"Key2\" AS INTEGER)", sqlTrunc);
    }

    [Fact]
    public void Coalesce_and_conditional_persist()
    {
        var (sqlCo, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => x.ParentId ?? 0)));
        Assert.Contains("COALESCE(\"ParentId\", @p0)", sqlCo);

        var (sqlCond, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key2, x => x.Key2 > 0 ? x.Key2 + 1 : x.Key2)));
        Assert.Contains("CASE WHEN", sqlCond);
        Assert.Contains("THEN (\"Key2\" + @p", sqlCond);
    }

    [Fact]
    public void Upsert_computed_uses_bare_column_not_alias()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b.Upsert<Customer>(u => u.MatchOn(x => x.Code).Update(x => x.Name, x => x.Name + "_upd").Insert(new Customer { Code = "A", Name = "X" })));
        Assert.Contains("\"Name\" = (\"Name\" || @p", sql);
        Assert.DoesNotContain("[t].", sql);
    }
}
