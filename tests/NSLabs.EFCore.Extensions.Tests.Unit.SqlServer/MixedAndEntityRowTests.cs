using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

public class MixedAndEntityRowTests
{
    [Fact]
    public void Multi_table_mixed_operations_preserve_order_and_share_parameters()
    {
        var cutoff = new DateTime(2026, 1, 1);

        var (sql, parameters) = Harness.GenerateSingle(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
            b.Update<Order>(op => op.Where(x => x.OrderNo == "A-1").Set(x => x.Amount, 10.5m));
            b.Delete<AuditLog>(op => op.Where(x => x.Created < cutoff));
        });

        Assert.Equal(
            "DECLARE @rc0 int; DECLARE @rc1 int; DECLARE @rc2 int; " +
            "UPDATE [Items] SET [Key1] = @p0 WHERE [Id] = @p1; SET @rc0 = @@ROWCOUNT; " +
            "UPDATE [Orders] SET [Amount] = @p2 WHERE [OrderNo] = @p3; SET @rc1 = @@ROWCOUNT; " +
            "DELETE FROM [AuditLogs] WHERE [Created] < @p4; SET @rc2 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0, @rc1 AS Op1, @rc2 AS Op2;",
            sql);

        var p = Harness.Params(parameters);
        Assert.Equal("Value1", p["@p0"]);
        Assert.Equal(6, p["@p1"]);
        Assert.Equal(10.5m, p["@p2"]);
        Assert.Equal("A-1", p["@p3"]);
        Assert.Equal(cutoff, p["@p4"]);
    }

    [Fact]
    public void Entity_rows_generate_pk_matched_full_row_updates_excluding_generated_columns()
    {
        var row = new Item
        {
            Id = 5,
            Key1 = "a",
            Key2 = 1,
            Key3 = 2,
            Status = OrderStatus.Pending,
            Active = true,
            ParentId = null,
            CreatedAt = new DateTime(2099, 1, 1)
        };

        var (sql, parameters) = Harness.GenerateSingle(b => b.Update<Item>([row]));

        Assert.Equal(
            "DECLARE @rc0 int; " +
            "UPDATE [Items] SET [Active] = @p0, [Key1] = @p1, [Key2] = @p2, [Key3] = @p3, [ParentId] = @p4, [Status] = @p5 WHERE [Id] = @p6; " +
            "SET @rc0 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0;",
            sql);

        var p = Harness.Params(parameters);
        Assert.Equal(true, p["@p0"]);
        Assert.Equal("a", p["@p1"]);
        Assert.Equal(1, p["@p2"]);
        Assert.Equal(2, p["@p3"]);
        Assert.Null(p["@p4"]);
        Assert.Equal(0, p["@p5"]);
        Assert.Equal(5, p["@p6"]);
    }

    [Fact]
    public void Entity_rows_with_custom_match_use_match_expression_per_row()
    {
        var rows = new[]
        {
            new Customer { Id = 100, Code = "A", Name = "X", Active = true },
            new Customer { Id = 200, Code = "B", Name = "Y", Active = false }
        };

        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Customer>(rows, (row, x) => x.Code == row.Code));

        Assert.Contains(
            "UPDATE [Customers] SET [Active] = @p0, [Code] = @p1, [Name] = @p2 WHERE [Code] = @p3; ",
            sql);
        Assert.Contains(
            "UPDATE [Customers] SET [Active] = @p4, [Code] = @p5, [Name] = @p6 WHERE [Code] = @p7; ",
            sql);
        Assert.Contains("SELECT @rc0 AS Op0, @rc1 AS Op1;", sql);
        Assert.DoesNotContain("[Id]", sql);

        var p = Harness.Params(parameters);
        Assert.Equal("A", p["@p1"]);
        Assert.Equal("A", p["@p3"]);
        Assert.Equal(false, p["@p4"]);
        Assert.Equal("B", p["@p7"]);
    }

    [Fact]
    public void Entity_rows_with_grouped_custom_match_preserve_tree_shape_and_operand_order()
    {
        var row = new Item
        {
            Id = 5,
            Key1 = "a",
            Key2 = 1,
            Key3 = 2,
            Status = OrderStatus.Pending,
            Active = true,
            ParentId = null,
            CreatedAt = new DateTime(2099, 1, 1)
        };

        var (rightGroupedSql, rightGroupedParameters) = Harness.GenerateSingle(b => b.Update<Item>(
            [row],
            (matchRow, x) => x.Id == matchRow.Id && (matchRow.Key1 == x.Key1 && x.Key2 == matchRow.Key2)));

        Assert.Equal(
            "DECLARE @rc0 int; " +
            "UPDATE [Items] SET [Active] = @p0, [Key1] = @p1, [Key2] = @p2, [Key3] = @p3, [ParentId] = @p4, [Status] = @p5 " +
            "WHERE ([Id] = @p6 AND (@p7 = [Key1] AND [Key2] = @p8)); " +
            "SET @rc0 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0;",
            rightGroupedSql);

        var rightGrouped = Harness.Params(rightGroupedParameters);
        Assert.Equal(5, rightGrouped["@p6"]);
        Assert.Equal("a", rightGrouped["@p7"]);
        Assert.Equal(1, rightGrouped["@p8"]);

        var (leftGroupedSql, _) = Harness.GenerateSingle(b => b.Update<Item>(
            [row],
            (matchRow, x) => (x.Id == matchRow.Id && matchRow.Key1 == x.Key1) && x.Key2 == matchRow.Key2));

        Assert.Equal(
            "DECLARE @rc0 int; " +
            "UPDATE [Items] SET [Active] = @p0, [Key1] = @p1, [Key2] = @p2, [Key3] = @p3, [ParentId] = @p4, [Status] = @p5 " +
            "WHERE (([Id] = @p6 AND @p7 = [Key1]) AND [Key2] = @p8); " +
            "SET @rc0 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0;",
            leftGroupedSql);
    }

    [Fact]
    public void Entity_rows_with_null_custom_match_emit_null_check_without_parameter()
    {
        var row = new Item
        {
            Id = 5,
            Key1 = "a",
            Key2 = 1,
            Key3 = 2,
            Status = OrderStatus.Pending,
            Active = true,
            ParentId = null,
            CreatedAt = new DateTime(2099, 1, 1)
        };

        var (sql, parameters) = Harness.GenerateSingle(b => b.Update<Item>(
            [row],
            (matchRow, x) => x.Id == matchRow.Id && x.ParentId == matchRow.ParentId));

        Assert.Equal(
            "DECLARE @rc0 int; " +
            "UPDATE [Items] SET [Active] = @p0, [Key1] = @p1, [Key2] = @p2, [Key3] = @p3, [ParentId] = @p4, [Status] = @p5 " +
            "WHERE ([Id] = @p6 AND [ParentId] IS NULL); " +
            "SET @rc0 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0;",
            sql);
        Assert.Equal(7, parameters.Count);
        Assert.Equal(5, Harness.Params(parameters)["@p6"]);
        Assert.DoesNotContain(parameters, parameter => parameter.Name == "@p7");
    }

    [Fact]
    public void Entity_rows_with_relational_custom_match_use_general_fallback_semantics()
    {
        var row = new Item
        {
            Id = 5,
            Key1 = "a",
            Key2 = 1,
            Key3 = 2,
            Status = OrderStatus.Pending,
            Active = true,
            ParentId = null,
            CreatedAt = new DateTime(2099, 1, 1)
        };

        var (sql, parameters) = Harness.GenerateSingle(b => b.Update<Item>(
            [row],
            (matchRow, x) => x.Id == matchRow.Id && x.Key2 > matchRow.Key2));

        Assert.Equal(
            "DECLARE @rc0 int; " +
            "UPDATE [Items] SET [Active] = @p0, [Key1] = @p1, [Key2] = @p2, [Key3] = @p3, [ParentId] = @p4, [Status] = @p5 " +
            "WHERE ([Id] = @p6 AND [Key2] > @p7); " +
            "SET @rc0 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0;",
            sql);

        var p = Harness.Params(parameters);
        Assert.Equal(5, p["@p6"]);
        Assert.Equal(1, p["@p7"]);
    }

    [Fact]
    public void Entity_rows_with_or_custom_match_preserve_general_fallback_grouping()
    {
        var row = new Customer { Id = 100, Code = "A", Name = "X", Active = true };

        var (sql, parameters) = Harness.GenerateSingle(b => b.Update<Customer>(
            [row],
            (matchRow, x) => x.Code == matchRow.Code || x.Name == matchRow.Name));

        Assert.Equal(
            "DECLARE @rc0 int; " +
            "UPDATE [Customers] SET [Active] = @p0, [Code] = @p1, [Name] = @p2 WHERE ([Code] = @p3 OR [Name] = @p4); " +
            "SET @rc0 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0;",
            sql);

        var p = Harness.Params(parameters);
        Assert.Equal("A", p["@p3"]);
        Assert.Equal("X", p["@p4"]);
    }

    [Fact]
    public void Entity_rows_with_custom_match_retain_tph_discriminator()
    {
        var row = new Cat { PetId = 7, Name = "Milo", LivesLeft = 8 };

        var (sql, parameters) = Harness.GenerateSingle(b => b.Update<Cat>(
            [row],
            (matchRow, x) => x.Name == matchRow.Name));

        Assert.Equal(
            "DECLARE @rc0 int; " +
            "UPDATE [Pets] SET [Name] = @p0, [LivesLeft] = @p1 WHERE [Name] = @p2 AND [PetType] = @p3; " +
            "SET @rc0 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0;",
            sql);

        var p = Harness.Params(parameters);
        Assert.Equal("Milo", p["@p0"]);
        Assert.Equal("Milo", p["@p2"]);
        Assert.Equal("Cat", p["@p3"]);
    }
}
