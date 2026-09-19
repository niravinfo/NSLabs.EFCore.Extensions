namespace NSLabs.EFCore.Extensions.Tests.Unit.Npgsql;

public class NpgsqlLargeListGoldenSqlTests
{
    [Fact]
    public void Large_list_is_one_array_param()
    {
        var ids = Enumerable.Range(1, 5000).ToList();
        var (sql, p) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("\"Id\" = ANY (@p", sql);
        Assert.DoesNotContain(" IN (", sql);
        Assert.Equal(2, p.Count);

        var array = p.Single(v => v.Value is int[]).Value as int[];
        Assert.NotNull(array);
        Assert.Equal(ids, array);
    }

    [Fact]
    public void Array_param_types_follow_converted_values()
    {
        var longs = new[] { 1L, 2L };
        var (_, pl) = NpgsqlHarness.GenerateSingle(
            b => b.Update<AuditLog>(op => op.Where(x => longs.Contains(x.Id)).Set(x => x.Created, new DateTime(2026, 1, 1))));
        Assert.IsType<long[]>(pl.Single(v => v.Value is Array).Value);

        var strings = new[] { "a", "b" };
        var (_, ps) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => strings.Contains(x.Key1)).Set(x => x.Key3, 1)));
        Assert.IsType<string[]>(ps.Single(v => v.Value is Array).Value);

        var decimals = new[] { 1.5m, 2.5m };
        var (_, pd) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Order>(op => op.Where(x => decimals.Contains(x.Amount)).Set(x => x.Status, OrderStatus.Shipped)));
        Assert.IsType<decimal[]>(pd.Single(v => v.Value is Array).Value);

        var statuses = new[] { OrderStatus.Pending, OrderStatus.Shipped };
        var (_, pe) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => statuses.Contains(x.Status)).Set(x => x.Key3, 1)));
        Assert.IsType<int[]>(pe.Single(v => v.Value is Array).Value);

        var dates = new[] { new DateTime(2026, 1, 1), new DateTime(2026, 1, 2) };
        var (_, pdt) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => dates.Contains(x.CreatedAt)).Set(x => x.Key3, 1)));
        Assert.IsType<DateTime[]>(pdt.Single(v => v.Value is Array).Value);
    }

    [Fact]
    public void Null_in_list_adds_or_is_null_on_nullable_column()
    {
        var ids = new List<int?> { 1, null };
        var (sql, p) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("(\"ParentId\" = ANY (@p", sql);
        Assert.Contains("OR \"ParentId\" IS NULL", sql);
        Assert.Equal(2, p.Count);

        // Array holds non-nulls only (static fold of EF's runtime array_position check).
        var array = p.Single(v => v.Value is Array).Value as int[];
        Assert.NotNull(array);
        Assert.Equal(new[] { 1 }, array);
    }

    [Fact]
    public void Null_in_list_is_dropped_on_non_nullable_column()
    {
        var ids = new List<int?> { 1, null };
        var (sql, p) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("\"Id\" = ANY (@p", sql);
        Assert.DoesNotContain("IS NULL", sql);
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void All_null_list_renders_is_null_with_zero_list_params()
    {
        var ids = new List<int?> { null };
        var (sql, p) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("\"ParentId\" IS NULL", sql);
        Assert.DoesNotContain("ANY", sql);
        Assert.Equal(1, p.Count);
    }

    [Fact]
    public void Empty_list_renders_false()
    {
        var ids = Array.Empty<int>();
        var (sql, _) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("1=0", sql);
    }

    [Fact]
    public void Negated_null_in_list_on_nullable_column()
    {
        var ids = new List<int?> { 1, null };
        var (sql, p) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => !ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("NOT (\"ParentId\" = ANY (@p", sql);
        Assert.Contains("AND \"ParentId\" IS NOT NULL", sql);
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void Negated_nullfree_list_on_nullable_column_includes_nulls()
    {
        var ids = new List<int?> { 1, 2 };
        var (sql, p) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => !ids.Contains(x.ParentId)).Set(x => x.Key3, 1)));

        Assert.Contains("NOT (\"ParentId\" = ANY (@p", sql);
        Assert.Contains("OR \"ParentId\" IS NULL", sql);
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void Negated_empty_list_renders_true()
    {
        var ids = Array.Empty<int>();
        var (sql, _) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => !ids.Contains(x.Id)).Set(x => x.Key3, 1)));

        Assert.Contains("1=1", sql);
        Assert.DoesNotContain("NOT (1=0)", sql);
    }

    [Fact]
    public void Byte_array_list_stays_multi_param()
    {
        // v1 byte[] exception (List<object> vehicle: the test model has no varbinary
        // column, but the element-type rule operates on values, not the model).
        var blobs = new List<object> { new byte[] { 1 }, new byte[] { 2 } };
        var (sql, p) = NpgsqlHarness.GenerateSingle(
            b => b.Update<Item>(op => op.Where(x => blobs.Contains(x.Key1)).Set(x => x.Key3, 1)));

        // Plain IN, not ANY; live byte[]-IN comparison stays out of scope for v1.
        Assert.Contains("\"Key1\" IN (", sql);
        Assert.DoesNotContain("ANY", sql);
        Assert.Equal(3, p.Count);
    }

    [Fact]
    public void Upsert_guard_in_uses_qualified_any()
    {
        var codes = Enumerable.Range(0, 150).Select(i => $"C{i}").ToList();
        var (sql, p) = NpgsqlHarness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .UpdateWhen(x => codes.Contains(x.Code))
                .Insert(new Customer { Code = "A", Name = "X" })));

        Assert.Contains("\"Customers\".\"Code\" = ANY (@p", sql);
        Assert.Single(p.Where(v => v.Value is string[]));
    }
}
