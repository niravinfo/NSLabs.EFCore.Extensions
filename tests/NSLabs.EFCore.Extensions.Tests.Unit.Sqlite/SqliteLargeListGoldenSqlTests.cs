using System.Text.Json;

namespace NSLabs.EFCore.Extensions.Tests.Unit.Sqlite;

public class SqliteLargeListGoldenSqlTests
{
    [Fact]
    public void Small_nullfree_in_is_byte_identical_to_before()
    {
        var ids = new[] { 1, 2, 3 };
        var (sql, p) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("\"Id\" IN (@p1, @p2, @p3)", sql);
        Assert.DoesNotContain("json_each", sql);
        Assert.Equal(4, p.Count);
    }

    [Fact]
    public void Large_list_uses_single_json_each_param()
    {
        var ids = Enumerable.Range(1, 51).ToList();
        var (sql, p) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("json_each(@p", sql);
        Assert.DoesNotContain("IN (@p", sql);
        Assert.Equal(2, p.Count);

        var payload = p.Single(v => v.Value is string s && s.StartsWith('[')).Value as string;
        Assert.NotNull(payload);
        Assert.Equal(ids, JsonSerializer.Deserialize<List<int>>(payload));
    }

    [Fact]
    public void Overflow_backstop_fires_below_threshold()
    {
        // 40 ids stay slow by threshold (<= 50) but 40 + 6 SETs overflow the
        // budget of 45, so the backstop must flip the IN to its 1-param shape.
        var ids = Enumerable.Range(1, 40).ToList();
        var (sql, p) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => ids.Contains(x.Id))
                .Set(x => x.Key1, "v")
                .Set(x => x.Key2, 2)
                .Set(x => x.Key3, 3)
                .Set(x => x.Status, OrderStatus.Shipped)
                .Set(x => x.Active, true)
                .Set(x => x.ParentId, 5)),
            new BulkExecuteOptions { MaxParametersPerCommand = 45 });

        Assert.Contains("json_each(@p", sql);
        Assert.Equal(7, p.Count);
    }

    [Fact]
    public void Large_list_ignores_999_clamp()
    {
        // 5000 ids collapse to 1 param, far under the 999 clamp — no throw.
        var ids = Enumerable.Range(1, 5000).ToList();
        var chunks = SqliteHarness.Generate(
            b => b.Delete<Item>(op => op.Where(x => ids.Contains(x.Id))));

        Assert.Single(chunks);
        Assert.Contains("json_each(@p", chunks[0].CommandText);
        Assert.Single(chunks[0].Parameters);
    }

    [Fact]
    public void Null_in_list_adds_or_is_null_on_nullable_column()
    {
        var ids = new List<int?> { 1, null };
        var (sql, p) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("(\"ParentId\" IN (@p1) OR \"ParentId\" IS NULL)", sql);
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void Null_in_list_is_dropped_on_non_nullable_column()
    {
        var ids = new List<int?> { 1, null };
        var (sql, p) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("\"Id\" IN (@p1)", sql);
        Assert.DoesNotContain("IS NULL", sql);
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void All_null_list_renders_is_null_with_zero_list_params()
    {
        var ids = new List<int?> { null };
        var (sql, p) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("\"ParentId\" IS NULL", sql);
        Assert.DoesNotContain("IN (", sql);
        Assert.Equal(1, p.Count);
    }

    [Fact]
    public void Empty_list_renders_false()
    {
        var ids = Array.Empty<int>();
        var (sql, _) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("1=0", sql);
    }

    [Fact]
    public void Negated_null_in_list_on_nullable_column()
    {
        var ids = new List<int?> { 1, null };
        var (sql, p) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => !ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("\"ParentId\" NOT IN (@p1) AND \"ParentId\" IS NOT NULL", sql);
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void Negated_nullfree_list_on_nullable_column_includes_nulls()
    {
        var ids = new List<int?> { 1, 2 };
        var (sql, _) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => !ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("(\"ParentId\" NOT IN (@p1, @p2) OR \"ParentId\" IS NULL)", sql);
    }

    [Fact]
    public void Negated_empty_list_renders_true()
    {
        var ids = Array.Empty<int>();
        var (sql, _) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => !ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("1=1", sql);
        Assert.DoesNotContain("NOT (1=0)", sql);
    }

    [Fact]
    public void Large_null_in_list_strips_null_from_json_payload()
    {
        var ids = Enumerable.Range(1, 51).Select(i => (int?)i).Append((int?)null).ToList();
        var (sql, p) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("json_each(@p", sql);
        Assert.Contains("OR \"ParentId\" IS NULL", sql);
        Assert.Equal(2, p.Count);

        var payload = p.Single(v => v.Value is string s && s.StartsWith('[')).Value as string;
        Assert.NotNull(payload);
        var roundTripped = JsonSerializer.Deserialize<List<int?>>(payload);
        Assert.NotNull(roundTripped);
        Assert.True(roundTripped.Count == 51, "JSON payload must hold exactly the 51 non-null ids.");
        Assert.DoesNotContain(null, roundTripped);
    }

    [Fact]
    public void Multiple_large_lists_each_get_one_param()
    {
        var idsA = Enumerable.Range(1, 60).ToList();
        var idsB = Enumerable.Range(1000, 60).ToList();
        var (sql, p) = SqliteHarness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => idsA.Contains(x.Id) && idsB.Contains(x.Key2))
                .Set(x => x.Key3, 1)));

        Assert.Equal(2, sql.Split("json_each(", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, p.Count);
    }

    [Fact]
    public void Upsert_guard_large_in_uses_json_each()
    {
        var codes = Enumerable.Range(0, 60).Select(i => $"C{i}").ToList();
        var chunks = SqliteHarness.Generate(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .UpdateWhen(x => codes.Contains(x.Code))
                .Insert(new Customer { Code = "A", Name = "X" })));

        Assert.Single(chunks);
        Assert.Contains("json_each(@p", chunks[0].CommandText);
    }
}
