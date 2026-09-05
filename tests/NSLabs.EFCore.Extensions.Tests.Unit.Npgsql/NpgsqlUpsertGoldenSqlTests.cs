using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.Npgsql;

public class NpgsqlUpsertGoldenSqlTests
{
    [Fact]
    public void Single_upsert_generates_insert_on_conflict()
    {
        var (sql, _) = NpgsqlHarness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .Insert(new Customer { Code = "A", Name = "X", Active = true })));

        Assert.Contains("INSERT INTO \"Customers\"", sql);
        Assert.Contains("ON CONFLICT (\"Code\") DO UPDATE SET", sql);
        Assert.Contains("excluded.", sql);
        Assert.DoesNotContain("MERGE", sql);
        Assert.DoesNotContain("HOLDLOCK", sql);
    }

    [Fact]
    public void Upsert_with_guard_generates_qualified_where_clause()
    {
        var (sql, _) = NpgsqlHarness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .UpdateWhen(x => x.Active)
                .Insert(new Customer { Code = "A", Name = "X", Active = true })));

        Assert.Contains("ON CONFLICT (\"Code\") DO UPDATE SET", sql);
        // PG guard must qualify target as "Table"."Col" and use TRUE
        Assert.Contains("WHERE \"Customers\".\"Active\" = TRUE", sql);
        Assert.DoesNotContain("= 1", sql);
    }

    [Fact]
    public void Upsert_with_explicit_set_uses_constant_param()
    {
        var (sql, p) = NpgsqlHarness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .Update(x => x.Name, "Fixed")
                .Insert(new Customer { Code = "A", Name = "X" })));

        Assert.Contains("\"Name\" = @p", sql);
        Assert.Contains("Fixed", p.Select(v => v.Value?.ToString()));
    }

    [Fact]
    public void Upsert_with_computed_set_uses_qualified_target_column()
    {
        var (sql, _) = NpgsqlHarness.GenerateSingle(b => b
            .Upsert<Order>(u => u
                .MatchOn(x => x.OrderNo)
                .Update(x => x.Amount, x => x.Amount * 1.1m)
                .Insert(new Order { OrderNo = "O-1", Amount = 100m, Status = OrderStatus.Pending })));

        // Computed RHS must reference qualified target column
        Assert.Contains("\"Amount\" = (\"Orders\".\"Amount\" * @p", sql);
        Assert.DoesNotContain("[t].[Amount]", sql);
        Assert.DoesNotContain("[s].", sql);
    }

    [Fact]
    public void Upsert_multi_row_uses_multiple_values_tuples()
    {
        var chunks = NpgsqlHarness.Generate(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .Insert(new[]
                {
                    new Customer { Code = "A", Name = "X" },
                    new Customer { Code = "B", Name = "Y" }
                })));

        Assert.Single(chunks);
        var sql = NpgsqlHarness.Normalize(chunks[0].CommandText);
        Assert.Contains("VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)", sql);
    }

    [Fact]
    public void Upsert_zero_rows_creates_no_op_chunk()
    {
        var chunks = NpgsqlHarness.Generate(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .Insert(Array.Empty<Customer>())));

        Assert.Single(chunks);
        Assert.Contains("zero-row", chunks[0].CommandText);
        Assert.Empty(chunks[0].Parameters);
    }

    [Fact]
    public void Composite_conflict_target_generates_multi_col_on_conflict()
    {
        var (sql, _) = NpgsqlHarness.GenerateSingle(b => b
            .Upsert<Item>(u => u
                .MatchOn(x => new { x.Key1, x.Key2 })
                .Insert(new Item { Key1 = "A", Key2 = 1 })));

        Assert.Contains("ON CONFLICT (\"Key1\", \"Key2\")", sql);
    }

    [Fact]
    public void Upsert_with_tph_discriminator_included()
    {
        var (sql, p) = NpgsqlHarness.GenerateSingle(b => b
            .Upsert<Cat>(u => u
                .MatchOn(x => x.PetId)
                .Insert(new Cat { PetId = 1, Name = "Whiskers", LivesLeft = 9 })));

        // Discriminator column should be in insert list for TPH
        Assert.Contains("\"PetType\"", sql);
        Assert.Contains("Cat", p.Select(v => v.Value?.ToString()));
    }

    [Fact]
    public void Upsert_chunking_splits_rows_when_exceeding_budget()
    {
        var chunks = NpgsqlHarness.Generate(b => b
            .Upsert<Customer>(u => u
                .MatchOn(x => x.Code)
                .Insert(new[]
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
