using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.Sqlite;

public class SqliteUpdateGoldenSqlTests
{
    [Fact]
    public void Single_update_by_id_matches_golden_sql_and_parameters()
    {
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 6)
                .Set(x => x.Key1, "Value1")
                .Set(x => x.Key2, 5)));

        Assert.Equal(
            "UPDATE \"Items\" SET \"Key1\" = @p0, \"Key2\" = @p1 WHERE \"Id\" = @p2;",
            sql);

        var p = SqliteHarness.Params(parameters);
        Assert.Equal("Value1", p["@p0"]);
        Assert.Equal(5, p["@p1"]);
        Assert.Equal(6, p["@p2"]);
    }

    [Fact]
    public void Two_updates_each_become_separate_chunks()
    {
        var chunks = SqliteHarness.Generate(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key3, 7));
            b.Update<Item>(op => op
                .Where(x => x.Key1 == "Old" && x.Key2 == -1)
                .Set(x => x.Key1, "New"));
        });

        // SQLite per-unit chunks = 2
        Assert.Equal(2, chunks.Count);
        Assert.Equal("UPDATE \"Items\" SET \"Key3\" = @p0 WHERE \"Id\" = @p1;", SqliteHarness.Normalize(chunks[0].CommandText));
        Assert.Equal("UPDATE \"Items\" SET \"Key1\" = @p0 WHERE (\"Key1\" = @p1 AND \"Key2\" = @p2);", SqliteHarness.Normalize(chunks[1].CommandText));

        // Params restart per chunk
        Assert.Equal(7, SqliteHarness.Params(chunks[0].Parameters)["@p0"]);
        Assert.Equal(1, SqliteHarness.Params(chunks[0].Parameters)["@p1"]);
        Assert.Equal("New", SqliteHarness.Params(chunks[1].Parameters)["@p0"]);
    }

    [Fact]
    public void Quoting_uses_double_quotes_not_brackets()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "v")));
        Assert.Contains("\"Items\"", sql);
        Assert.Contains("\"Key1\"", sql);
        Assert.DoesNotContain("[Items]", sql);
        Assert.DoesNotContain("[Key1]", sql);
    }

    [Fact]
    public void Delete_generates_correct_sql()
    {
        var (sql, p) = SqliteHarness.GenerateSingle(b => b
            .Delete<AuditLog>(op => op.Where(x => x.Id == 5)));
        Assert.Equal("DELETE FROM \"AuditLogs\" WHERE \"Id\" = @p0;", sql);
        Assert.Equal((long)5, p.First().Value);
    }

    [Fact]
    public void Null_comparison_renders_is_null()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.ParentId == null)
                .Set(x => x.Key3, 9)));
        Assert.Contains("\"ParentId\" IS NULL", sql);
    }

    [Fact]
    public void Boolean_member_renders_as_integer_equality()
    {
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Active && x.Status == OrderStatus.Pending)
                .Set(x => x.Key1, "x")));
        Assert.Contains("(\"Active\" = 1 AND \"Status\" = @p1)", sql);
        Assert.Equal(0, SqliteHarness.Params(parameters)["@p1"]);
    }

    [Fact]
    public void Derived_tph_entity_gets_discriminator_predicate()
    {
        var (sql, parameters) = SqliteHarness.GenerateSingle(b => b
            .Update<Cat>(op => op
                .Where(x => x.PetId == 3)
                .Set(x => x.LivesLeft, 8)));
        Assert.Contains("UPDATE \"Pets\" SET \"LivesLeft\" = @p0 WHERE \"PetId\" = @p1 AND \"PetType\" = @p2;", sql);
        Assert.Equal("Cat", SqliteHarness.Params(parameters)["@p2"]);
    }

    [Fact]
    public void Multi_table_batch_each_chunk_handles_one_table()
    {
        var chunks = SqliteHarness.Generate(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "a"));
            b.Update<Order>(op => op.Where(x => x.OrderNo == "O1").Set(x => x.Amount, 5m));
        });
        Assert.Equal(2, chunks.Count);
        Assert.Contains("\"Items\"", chunks[0].CommandText);
        Assert.Contains("\"Orders\"", chunks[1].CommandText);
    }
}
