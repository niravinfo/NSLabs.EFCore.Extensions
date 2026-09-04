using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

public class UpsertGoldenSqlTests
{
    [Fact]
    public void Single_row_upsert_by_custom_conflict_target_matches_golden_sql()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .On(x => x.Code)
                .Values(new Customer { Code = "A", Name = "X", Active = true })));

        Assert.Equal(
            "DECLARE @rc0 int; " +
            "MERGE INTO [Customers] WITH (HOLDLOCK) AS [t] " +
            "USING (VALUES (@p0, @p1, @p2)) AS [s] ([Code], [Active], [Name]) " +
            "ON [t].[Code] = [s].[Code] " +
            "WHEN MATCHED THEN UPDATE SET [Active] = [s].[Active], [Name] = [s].[Name] " +
            "WHEN NOT MATCHED THEN INSERT ([Code], [Active], [Name]) VALUES ([s].[Code], [s].[Active], [s].[Name]); " +
            "SET @rc0 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0;",
            sql);

        var p = Harness.Params(parameters);
        Assert.Equal("A", p["@p0"]);
        Assert.Equal(true, p["@p1"]);
        Assert.Equal("X", p["@p2"]);
    }

    [Fact]
    public void Multi_row_upsert_emits_one_tuple_per_row()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .On(x => x.Code)
                .Values(new[]
                {
                    new Customer { Code = "A", Name = "X", Active = true },
                    new Customer { Code = "B", Name = "Y", Active = false }
                })));

        Assert.Contains(
            "USING (VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)) AS [s] ([Code], [Active], [Name])",
            sql);
        Assert.Matches(@"WHEN NOT MATCHED THEN INSERT \([^\)]+\) VALUES \(.*\);", sql);

        var p = Harness.Params(parameters);
        Assert.Equal("A", p["@p0"]);
        Assert.Equal("B", p["@p3"]);
        Assert.Equal("Y", p["@p5"]);
    }

    [Fact]
    public void Default_conflict_target_is_primary_key_and_excludes_generated_columns()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Upsert<Order>(u => u
                .Values(new Order { OrderNo = "O-1", Amount = 12.5m, Status = OrderStatus.Shipped })));

        Assert.Contains("AS [s] ([OrderNo], [Amount], [Status])", sql);
        Assert.Contains("ON [t].[OrderNo] = [s].[OrderNo]", sql);
        Assert.DoesNotContain("[Id]", sql);

        var p = Harness.Params(parameters);
        Assert.Equal("O-1", p["@p0"]);
        Assert.Equal(12.5m, p["@p1"]);
        Assert.Equal(1, p["@p2"]);
    }

    [Fact]
    public void Guard_is_emitted_against_target_alias()
    {
        var (sql, _) = Harness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .On(x => x.Code)
                .WhenMatched(x => !x.Active)
                .Values(new Customer { Code = "A", Name = "X", Active = true })));

        Assert.Contains("ON [t].[Code] = [s].[Code] WHEN MATCHED AND NOT ([t].[Active] = 1) THEN UPDATE SET", sql);
    }

    [Fact]
    public void Explicit_set_constants_replace_row_wide_update_payload()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Upsert<Item>(u => u
                .On(x => x.Id)
                .Set(x => x.Key3, 9)
                .Values(new Item { Id = 42, Key1 = "k" })));

        Assert.Contains(
            "USING (VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6)) AS [s] ([Id], [Active], [Key1], [Key2], [Key3], [ParentId], [Status])",
            sql);
        Assert.Contains("WHEN MATCHED THEN UPDATE SET [Key3] = @p7 WHEN NOT MATCHED", sql);

        var p = Harness.Params(parameters);
        Assert.Equal(42, p["@p0"]);
        Assert.Equal("k", p["@p2"]);
        Assert.Null(p["@p5"]);
        Assert.Equal(9, p["@p7"]);
    }

    [Fact]
    public void Tph_derived_upsert_includes_discriminator_column_in_insert()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Upsert<Cat>(u => u
                .On(x => x.PetId)
                .Values(new Cat { PetId = 7, Name = "Tom", LivesLeft = 9 })));

        Assert.Contains("AS [s] ([PetId], [Name], [LivesLeft], [PetType])", sql);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT ([PetId], [Name], [LivesLeft], [PetType]) VALUES ([s].[PetId], [s].[Name], [s].[LivesLeft], [s].[PetType]);", sql);

        var p = Harness.Params(parameters);
        Assert.Equal(7, p["@p0"]);
        Assert.Equal("Tom", p["@p1"]);
        Assert.Equal(9, p["@p2"]);
        Assert.Equal("Cat", p["@p3"]);
    }

    [Fact]
    public void Zero_row_upsert_reports_zero_without_a_round_trip_statement()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Upsert<Customer>(u => u.On(x => x.Code)));

        Assert.Equal(
            "DECLARE @rc0 int; SET @rc0 = 0; SELECT @rc0 AS Op0;",
            sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void Mixed_update_and_upsert_share_parameter_counter_and_result_set()
    {
        var (sql, parameters) = Harness.GenerateSingle(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "u"));
            b.Upsert<Customer>(u => u.On(x => x.Code).Values(new Customer { Code = "A", Name = "X", Active = true }));
        });

        Assert.Contains("UPDATE [Items] SET [Key1] = @p0 WHERE [Id] = @p1; SET @rc0 = @@ROWCOUNT;", sql);
        Assert.Contains("USING (VALUES (@p2, @p3, @p4))", sql);
        Assert.EndsWith("SELECT @rc0 AS Op0, @rc1 AS Op1;", sql);

        var p = Harness.Params(parameters);
        Assert.Equal("A", p["@p2"]);
    }
}
