using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.Sqlite;

public class SqliteUpsertGoldenSqlTests
{
    [Fact]
    public void Single_upsert_generates_insert_on_conflict()
    {
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .On(x => x.Code)
                .Values(new Customer { Code = "A", Name = "X", Active = true })));

        Assert.Contains("INSERT INTO \"Customers\"", sql);
        Assert.Contains("ON CONFLICT (\"Code\") DO UPDATE SET", sql);
        Assert.Contains("excluded.", sql);
        Assert.DoesNotContain("MERGE", sql);
        Assert.DoesNotContain("HOLDLOCK", sql);
    }

    [Fact]
    public void Upsert_with_guard_generates_where_clause()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .On(x => x.Code)
                .WhenMatched(x => x.Active)
                .Values(new Customer { Code = "A", Name = "X", Active = true })));

        Assert.Contains("ON CONFLICT (\"Code\") DO UPDATE SET", sql);
        Assert.Contains("WHERE \"Active\" = 1", sql);
    }

    [Fact]
    public void Upsert_with_explicit_set_uses_constant_param()
    {
        var (sql, p) = SqliteHarness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .On(x => x.Code)
                .Set(x => x.Name, "Fixed")
                .Values(new Customer { Code = "A", Name = "X" })));

        Assert.Contains("\"Name\" = @p", sql);
        Assert.Contains("Fixed", p.Select(v => v.Value?.ToString()));
    }

    [Fact]
    public void Upsert_with_computed_set_uses_target_column()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b
            .Upsert<Order>(u => u
                .On(x => x.OrderNo)
                .Set(x => x.Amount, x => x.Amount * 1.1m)
                .Values(new Order { OrderNo = "O-1", Amount = 100m, Status = OrderStatus.Pending })));

        // Computed should reference target column without t. alias, using bare quoted col
        Assert.Contains("\"Amount\" = (\"Amount\" * @p", sql);
        Assert.DoesNotContain("[t].[Amount]", sql);
        Assert.DoesNotContain("[s].", sql);
    }

    [Fact]
    public void Upsert_multi_row_uses_multiple_values_tuples()
    {
        var chunks = SqliteHarness.Generate(b => b
            .Upsert<Customer>(u => u
                .On(x => x.Code)
                .Values(new[]
                {
                    new Customer { Code = "A", Name = "X" },
                    new Customer { Code = "B", Name = "Y" }
                })));

        Assert.Single(chunks);
        var sql = SqliteHarness.Normalize(chunks[0].CommandText);
        Assert.Contains("VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)", sql);
    }

    [Fact]
    public void Upsert_zero_rows_creates_no_op_chunk()
    {
        var chunks = SqliteHarness.Generate(b => b
            .Upsert<Customer>(u => u
                .On(x => x.Code)
                .Values(Array.Empty<Customer>())));

        Assert.Single(chunks);
        Assert.Contains("zero-row", chunks[0].CommandText);
        Assert.Empty(chunks[0].Parameters);
    }

    [Fact]
    public void Composite_conflict_target_generates_multi_col_on_conflict()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b
            .Upsert<Item>(u => u
                .On(x => new { x.Key1, x.Key2 })
                .Values(new Item { Key1 = "A", Key2 = 1 })));

        Assert.Contains("ON CONFLICT (\"Key1\", \"Key2\")", sql);
    }

    [Fact]
    public void Upsert_with_tph_discriminator_included()
    {
        var (sql, p) = SqliteHarness.GenerateSingle(b => b
            .Upsert<Cat>(u => u
                .On(x => x.PetId)
                .Values(new Cat { PetId = 1, Name = "Whiskers", LivesLeft = 9 })));

        // Discriminator column should be in insert list for TPH
        Assert.Contains("\"PetType\"", sql);
        Assert.Contains("Cat", p.Select(v => v.Value?.ToString()));
    }

    [Fact]
    public void Upsert_chunking_splits_rows_when_exceeding_budget()
    {
        var chunks = SqliteHarness.Generate(b => b
            .Upsert<Customer>(u => u
                .On(x => x.Code)
                .Values(new[]
                {
                    new Customer { Code = "A" },
                    new Customer { Code = "B" },
                    new Customer { Code = "C" }
                })), new BulkExecuteOptions { MaxParametersPerCommand = 8 });

        // 3 cols per row (Code, Name, Active) => 3*3=9 >8 so split into 2 chunks: 2 rows +1 row
        Assert.Equal(2, chunks.Count);
        Assert.Contains("VALUES (@p0", chunks[0].CommandText);
        Assert.Contains("VALUES (@p0", chunks[1].CommandText);
    }
}
