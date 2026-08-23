using EF.Core.Extensions.Internal;

namespace EF.Core.Extensions.Tests.Unit;

public class UpdateGoldenSqlTests
{
    [Fact]
    public void Single_update_by_id_matches_golden_sql_and_parameters()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 6)
                .Set(x => x.Key1, "Value1")
                .Set(x => x.Key2, 5)));

        Assert.Equal(
            "DECLARE @rc0 int; " +
            "UPDATE [Items] SET [Key1] = @p0, [Key2] = @p1 WHERE [Id] = @p2; " +
            "SET @rc0 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0;",
            sql);

        var p = Harness.Params(parameters);
        Assert.Equal("Value1", p["@p0"]);
        Assert.Equal(5, p["@p1"]);
        Assert.Equal(6, p["@p2"]);
    }

    [Fact]
    public void Two_updates_share_one_parameter_counter()
    {
        var (sql, parameters) = Harness.GenerateSingle(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key3, 7));
            b.Update<Item>(op => op
                .Where(x => x.Key1 == "Old" && x.Key2 == -1)
                .Set(x => x.Key1, "New"));
        });

        Assert.Equal(
            "DECLARE @rc0 int; DECLARE @rc1 int; " +
            "UPDATE [Items] SET [Key3] = @p0 WHERE [Id] = @p1; SET @rc0 = @@ROWCOUNT; " +
            "UPDATE [Items] SET [Key1] = @p2 WHERE ([Key1] = @p3 AND [Key2] = @p4); SET @rc1 = @@ROWCOUNT; " +
            "SELECT @rc0 AS Op0, @rc1 AS Op1;",
            sql);

        var p = Harness.Params(parameters);
        Assert.Equal(7, p["@p0"]);
        Assert.Equal(1, p["@p1"]);
        Assert.Equal("New", p["@p2"]);
        Assert.Equal("Old", p["@p3"]);
        Assert.Equal(-1, p["@p4"]);
    }

    [Fact]
    public void Captured_local_variable_is_parameterized()
    {
        var oldValue = "OldValue";
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Key1 == oldValue)
                .Set(x => x.Key3, 0)));

        Assert.Contains("WHERE [Key1] = @p1;", sql);
        Assert.Equal("OldValue", Harness.Params(parameters)["@p1"]);
    }

    [Fact]
    public void Null_comparison_renders_is_null()
    {
        var (sql, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.ParentId == null)
                .Set(x => x.Key3, 9)));

        Assert.Contains("WHERE [ParentId] IS NULL;", sql);
    }

    [Fact]
    public void Not_null_comparison_renders_is_not_null()
    {
        var (sql, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.ParentId != null)
                .Set(x => x.Key3, 9)));

        Assert.Contains("WHERE [ParentId] IS NOT NULL;", sql);
    }

    [Fact]
    public void Boolean_member_renders_as_bit_equality()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Active && x.Status == OrderStatus.Pending)
                .Set(x => x.Key1, "x")));

        Assert.Contains("WHERE ([Active] = 1 AND [Status] = @p1);", sql);
        Assert.Equal(0, Harness.Params(parameters)["@p1"]);
    }

    [Fact]
    public void Enum_set_value_uses_converted_underlying_value()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 2)
                .Set(x => x.Status, OrderStatus.Shipped)));

        Assert.Contains("SET [Status] = @p0", sql);
        Assert.Equal(1, Harness.Params(parameters)["@p0"]);
    }

    [Fact]
    public void Negated_boolean_renders_not()
    {
        var (sql, _) = Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => !x.Active)
                .Set(x => x.Key3, 1)));

        Assert.Contains("WHERE NOT ([Active] = 1);", sql);
    }

    [Fact]
    public void Derived_tph_entity_gets_discriminator_predicate()
    {
        var (sql, parameters) = Harness.GenerateSingle(b => b
            .Update<Cat>(op => op
                .Where(x => x.PetId == 3)
                .Set(x => x.LivesLeft, 8)));

        Assert.Contains("UPDATE [Pets] SET [LivesLeft] = @p0 WHERE [PetId] = @p1 AND [PetType] = @p2;", sql);
        var p = Harness.Params(parameters);
        Assert.Equal(3, p["@p1"]);
        Assert.Equal("Cat", p["@p2"]);
    }
}
