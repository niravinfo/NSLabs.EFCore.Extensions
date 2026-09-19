using System.Text.Json;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

public class LargeListGoldenSqlTests
{
    [Fact]
    public void Small_nullfree_in_is_byte_identical_to_before()
    {
        var ids = new[] { 1, 2, 3 };
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("[Id] IN (@p1, @p2, @p3)", sql);
        Assert.DoesNotContain("OPENJSON", sql);
        Assert.Equal(4, p.Count);
    }

    [Fact]
    public void Large_list_uses_single_openjson_param()
    {
        var ids = Enumerable.Range(1, 101).ToList();
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("OPENJSON(", sql);
        Assert.Contains("WITH ([Value] int '$')", sql);
        Assert.DoesNotContain("IN (@p", sql);
        Assert.Equal(2, p.Count);

        var payload = p.Single(v => v.Value is string s && s.StartsWith('[')).Value as string;
        Assert.NotNull(payload);
        Assert.Equal(ids, JsonSerializer.Deserialize<List<int>>(payload));
    }

    [Fact]
    public void Overflow_backstop_fires_below_threshold()
    {
        // 50 ids stay slow by threshold (<= 100) but 50 + 6 SETs overflow the
        // budget of 55, so the backstop must flip the IN to its 1-param shape.
        var ids = Enumerable.Range(1, 50).ToList();
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => ids.Contains(x.Id))
                .Set(x => x.Key1, "v")
                .Set(x => x.Key2, 2)
                .Set(x => x.Key3, 3)
                .Set(x => x.Status, OrderStatus.Shipped)
                .Set(x => x.Active, true)
                .Set(x => x.ParentId, 5)),
            new BulkExecuteOptions { MaxParametersPerCommand = 55 });

        Assert.Contains("OPENJSON(", sql);
        Assert.Equal(7, p.Count);
    }

    [Fact]
    public void Null_in_list_adds_or_is_null_on_nullable_column()
    {
        var ids = new List<int?> { 1, null };
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("([ParentId] IN (@p1) OR [ParentId] IS NULL)", sql);
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void Null_in_list_is_dropped_on_non_nullable_column()
    {
        var ids = new List<int?> { 1, null };
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("[Id] IN (@p1)", sql);
        Assert.DoesNotContain("IS NULL", sql);
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void All_null_list_renders_is_null_with_zero_list_params()
    {
        var ids = new List<int?> { null };
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("[ParentId] IS NULL", sql);
        Assert.DoesNotContain("IN (", sql);
        Assert.Equal(1, p.Count);
    }

    [Fact]
    public void Empty_list_renders_false()
    {
        var ids = Array.Empty<int>();
        var (sql, _) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("1=0", sql);
    }

    [Fact]
    public void Negated_null_in_list_on_nullable_column()
    {
        var ids = new List<int?> { 1, null };
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => !ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("[ParentId] NOT IN (@p1) AND [ParentId] IS NOT NULL", sql);
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void Negated_nullfree_list_on_nullable_column_includes_nulls()
    {
        var ids = new List<int?> { 1, 2 };
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => !ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("([ParentId] NOT IN (@p1, @p2) OR [ParentId] IS NULL)", sql);
        Assert.Equal(3, p.Count);
    }

    [Fact]
    public void Negated_empty_list_renders_true()
    {
        var ids = Array.Empty<int>();
        var (sql, _) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => !ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("1=1", sql);
        Assert.DoesNotContain("NOT (1=0)", sql);
    }

    [Fact]
    public void Large_null_in_list_strips_null_from_json_payload()
    {
        var ids = Enumerable.Range(1, 101).Select(i => (int?)i).Append((int?)null).ToList();
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("OPENJSON(", sql);
        Assert.Contains("OR [ParentId] IS NULL", sql);
        Assert.Equal(2, p.Count);

        var payload = p.Single(v => v.Value is string s && s.StartsWith('[')).Value as string;
        Assert.NotNull(payload);
        var roundTripped = JsonSerializer.Deserialize<List<int?>>(payload);
        Assert.NotNull(roundTripped);
        Assert.True(roundTripped.Count == 101, "JSON payload must hold exactly the 101 non-null ids.");
        Assert.DoesNotContain(null, roundTripped);
    }

    [Fact]
    public void Multiple_large_lists_each_get_one_param()
    {
        var idsA = Enumerable.Range(1, 150).ToList();
        var idsB = Enumerable.Range(1000, 150).ToList();
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op
                .Where(x => idsA.Contains(x.Id) && idsB.Contains(x.Key2))
                .Set(x => x.Key3, 1)));

        Assert.Equal(2, sql.Split("OPENJSON(", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, p.Count);
    }

    [Fact]
    public void Large_string_list_json_round_trips_special_chars()
    {
        var tricky = new[] { "a'b", "q\"q", "back\\slash", "ünïcodé✓", "tab\there" };
        var ids = Enumerable.Range(0, 101).Select(i => tricky[i % tricky.Length] + i).ToList();
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Key1)).Set(x => x.Key3, 1)));

        Assert.Contains("WITH ([Value] nvarchar(max) '$')", sql);
        Assert.Equal(2, p.Count);

        var payload = p.Single(v => v.Value is string s && s.StartsWith('[')).Value as string;
        Assert.NotNull(payload);
        Assert.Equal(ids, JsonSerializer.Deserialize<List<string>>(payload));
    }

    [Fact]
    public void Value_converted_enum_uses_store_type_in_with()
    {
        // Item.Status has HasConversion<int>(): the JSON holds converted ints,
        // so WITH must describe the store domain (int), not the CLR enum.
        var ids = new[] { OrderStatus.Pending, OrderStatus.Shipped }.Concat(
            Enumerable.Range(0, 150).Select(i => (OrderStatus)(i % 3))).ToList();
        var (sql, p) = Harness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Status)).Set(x => x.Key3, 1)));

        Assert.Contains("WITH ([Value] int '$')", sql);
        Assert.Equal(2, p.Count);

        var payload = p.Single(v => v.Value is string s && s.StartsWith('[')).Value as string;
        Assert.NotNull(payload);
        Assert.Equal(ids.Select(s => (int)s).ToList(), JsonSerializer.Deserialize<List<int>>(payload));
    }

    [Fact]
    public void Upsert_guard_large_in_uses_aliased_openjson()
    {
        var codes = Enumerable.Range(0, 150).Select(i => $"C{i}").ToList();
        var chunks = Harness.Generate(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .UpdateWhen(x => codes.Contains(x.Code))
                .Insert(new Customer { Code = "A", Name = "X" })));

        Assert.Single(chunks);
        var sql = chunks[0].CommandText;
        Assert.Contains("MERGE INTO", sql);
        Assert.Contains("[t].[Code] IN (SELECT [v].[Value] FROM OPENJSON(@p", sql);
        Assert.Contains("WITH ([Value] nvarchar(max) '$')", sql);

        var payload = chunks[0].Parameters.Single(v => v.Value is string s && s.StartsWith('[')).Value as string;
        Assert.NotNull(payload);
        Assert.Equal(codes, JsonSerializer.Deserialize<List<string>>(payload));
    }

    [Fact]
    public void Delete_with_5000_ids_is_one_chunk_one_param()
    {
        var ids = Enumerable.Range(1, 5000).ToList();
        var chunks = Harness.Generate(
            b => b.Delete<Item>(op => op.Where(x => ids.Contains(x.Id))));

        Assert.Single(chunks);
        Assert.Contains("OPENJSON(", chunks[0].CommandText);
        Assert.Single(chunks[0].Parameters);

        var payload = chunks[0].Parameters[0].Value as string;
        Assert.NotNull(payload);
        Assert.Equal(ids, JsonSerializer.Deserialize<List<int>>(payload));
    }
}
