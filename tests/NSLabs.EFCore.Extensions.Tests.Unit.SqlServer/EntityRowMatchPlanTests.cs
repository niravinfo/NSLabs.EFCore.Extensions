using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

/// <summary>
/// Differential guard for <see cref="EntityRowMatchPlan"/>.
///
/// The plan is a performance shortcut, so the only question that matters is whether it ever
/// produces something different from <see cref="LinqPredicateTranslator"/> — the path it
/// replaces. Every case below builds the same row twice: once through the plan, once through
/// the original per-row rewrite, then compares the two <see cref="SqlNode"/> trees
/// structurally. Identical trees mean identical generated SQL, because the generator is
/// deterministic given the tree.
/// </summary>
public class EntityRowMatchPlanTests
{
    private static TestDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer("Server=tcp:localhost,1433;Database=BulkExtensionsTest;User Id=test;Password=test;TrustServerCertificate=True;")
            .Options;
        return new Harness.SqlServerUnitTestDbContext(opts);
    }

    private static IEntityType ItemType(TestDbContext context) => context.Model.FindEntityType(typeof(Item))!;

    /// <summary>Runs the reference (pre-optimization) translation for a single row.</summary>
    private static SqlNode ReferenceTranslate<TEntity>(
        LambdaExpression match,
        IEntityType entityType,
        TEntity row)
        where TEntity : class
    {
        var rewritten = ParameterReplacer.Replace(match.Body, match.Parameters[0], Expression.Constant(row, typeof(TEntity)));
        var lambda = Expression.Lambda<Func<TEntity, bool>>(rewritten, match.Parameters[1]);
        return LinqPredicateTranslator.Translate(lambda, entityType, lambda.Parameters[0]);
    }

    private static void AssertSameTree(SqlNode expected, SqlNode actual, string because)
    {
        // Structural comparison only. The two paths necessarily build separate node
        // instances, so reference identity is not an equivalence criterion; the emitted
        // SQL depends on the tree's shape, operators, properties and parameter values.
        switch (expected)
        {
            case SqlBinaryNode e:
                var a = Assert.IsType<SqlBinaryNode>(actual);
                Assert.Equal(e.Operator, a.Operator);
                Assert.True(e.Left.GetType() == a.Left.GetType(), $"{because}: left operand is {a.Left.GetType().Name}, expected {e.Left.GetType().Name}");
                Assert.True(e.Right.GetType() == a.Right.GetType(), $"{because}: right operand is {a.Right.GetType().Name}, expected {e.Right.GetType().Name}");
                AssertSameTree(e.Left, a.Left, $"{because} [left]");
                AssertSameTree(e.Right, a.Right, $"{because} [right]");
                break;

            case SqlParameterNode ep:
                var ap = Assert.IsType<SqlParameterNode>(actual);
                Assert.Equal(ep.Value, ap.Value);
                Assert.Equal(ep.Value?.GetType(), ap.Value?.GetType());
                break;

            case SqlColumnNode ec:
                var ac = Assert.IsType<SqlColumnNode>(actual);
                Assert.Same(ec.Property, ac.Property);
                Assert.Equal(ec.ConvertedInTree, ac.ConvertedInTree);
                break;

            case SqlNullCheckNode en:
                var an = Assert.IsType<SqlNullCheckNode>(actual);
                Assert.Same(en.Property, an.Property);
                Assert.Equal(en.IsNotNull, an.IsNotNull);
                break;

            default:
                throw new NotSupportedException($"Unhandled node type {expected.GetType().Name} in the differential guard.");
        }
    }

    private static void AssertPlanMatchesReference<TEntity>(
        TestDbContext context,
        Expression<Func<TEntity, TEntity, bool>> match,
        params TEntity[] rows)
        where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))!;
        Expression<Func<TEntity, TEntity, bool>> boxed = match;

        var plan = EntityRowMatchPlan.TryCreate(boxed, entityType, boxed.Parameters[0], boxed.Parameters[1]);
        Assert.NotNull(plan);

        foreach (var row in rows)
        {
            var fast = plan!.Build(row);
            var reference = ReferenceTranslate(boxed, entityType, row);
            AssertSameTree(reference, fast, $"shape '{match}' on row {row}");
        }
    }

    [Fact]
    public void Single_equality_matches_reference_for_both_operand_orders()
    {
        using var context = CreateContext();

        AssertPlanMatchesReference(
            context,
            (row, x) => x.Id == row.Id,
            new Item { Id = 1 }, new Item { Id = 2 });

        // Reversed: the translator preserves operand order, so the plan must too.
        AssertPlanMatchesReference(
            context,
            (row, x) => row.Id == x.Id,
            new Item { Id = 1 }, new Item { Id = 2 });
    }

    [Fact]
    public void Conjunction_matches_reference_including_left_nested_shape()
    {
        using var context = CreateContext();

        AssertPlanMatchesReference(
            context,
            (row, x) => x.Id == row.Id && x.Key1 == row.Key1,
            new Item { Id = 1, Key1 = "a" },
            new Item { Id = 2, Key1 = "b" });

        // Left-nested: ((a && b) && c) must keep its nesting, not flatten.
        AssertPlanMatchesReference(
            context,
            (row, x) => x.Id == row.Id && x.Key1 == row.Key1 && x.Key2 == row.Key2,
            new Item { Id = 1, Key1 = "a", Key2 = 7 });
    }

    [Fact]
    public void Null_row_values_match_reference_null_check_rewrite()
    {
        using var context = CreateContext();

        // Equality against null becomes IS NULL, not a comparison — the case a naive
        // "always emit Eq" fast path would get wrong.
        AssertPlanMatchesReference(
            context,
            (row, x) => x.Key1 == row.Key1,
            new Item { Key1 = null! },
            new Item { Key1 = "a" },
            new Item { Key1 = "" });

        AssertPlanMatchesReference(
            context,
            (row, x) => x.Key1 != row.Key1,
            new Item { Key1 = null! },
            new Item { Key1 = "a" });

        AssertPlanMatchesReference(
            context,
            (row, x) => x.ParentId == row.ParentId,
            new Item { ParentId = null },
            new Item { ParentId = 5 });
    }

    [Fact]
    public void Relational_and_disjunction_shapes_match_reference()
    {
        using var context = CreateContext();

        AssertPlanMatchesReference(context, (row, x) => x.Key2 > row.Key2, new Item { Key2 = 3 }, new Item { Key2 = 0 });
        AssertPlanMatchesReference(context, (row, x) => row.Key2 <= x.Key2, new Item { Key2 = 3 });
        AssertPlanMatchesReference(context, (row, x) => x.CreatedAt >= row.CreatedAt, new Item { CreatedAt = new DateTime(2026, 1, 1) });
        AssertPlanMatchesReference(context, (row, x) => x.Id == row.Id || x.Key1 == row.Key1, new Item { Id = 1, Key1 = "a" });

        // Relational against a null value: no IS NULL rewrite, the raw parameter is kept.
        AssertPlanMatchesReference(context, (row, x) => x.Key2 < row.Key2, new Item { Key2 = 0 });
    }

    [Fact]
    public void Enum_comparisons_decline_the_plan_and_keep_working_through_the_fallback()
    {
        using var context = CreateContext();
        var entityType = ItemType(context);

        // Status is an enum with a value converter, so C# lowers x.Status == row.Status to
        // Equal(Convert(x.Status), Convert(row.Status)). The plan requires a member to sit
        // directly on a parameter, so it declines rather than modelling the Convert wrapper
        // (which the translator treats specially via SqlColumnNode.ConvertedInTree). Declining
        // is the safe outcome: the row takes the original rewrite and behaves exactly as
        // before. Documented coverage limit, not a correctness hole.
        Expression<Func<Item, Item, bool>> match = (row, x) => x.Status == row.Status;
        var plan = EntityRowMatchPlan.TryCreate(match, entityType, match.Parameters[0], match.Parameters[1]);
        Assert.True(plan is null);

        // The fallback still translates, and still applies the ConvertedInTree rule (the
        // parameter keeps the raw enum rather than being converted to its store type).
        var fallback = ReferenceTranslate(match, entityType, new Item { Status = OrderStatus.Delivered });
        var parameter = Assert.IsType<SqlBinaryNode>(fallback);
        var column = Assert.IsType<SqlColumnNode>(parameter.Left);
        Assert.True(column.ConvertedInTree);
        Assert.Equal(OrderStatus.Delivered, Assert.IsType<SqlParameterNode>(parameter.Right).Value);
    }

    [Fact]
    public void Direct_member_comparisons_of_common_column_types_get_a_plan()
    {
        using var context = CreateContext();
        var entityType = ItemType(context);

        // The shapes the plan is meant to accelerate: int, string, decimal, DateTime, bool
        // and nullable int all lower to a member directly on the parameter (no Convert).
        Expression<Func<Item, Item, bool>>[] plannable =
        [
            (row, x) => x.Key1 == row.Key1,
            (row, x) => x.Active == row.Active,
            (row, x) => x.ParentId == row.ParentId,
            (row, x) => x.CreatedAt == row.CreatedAt,
            (row, x) => x.Id == row.Id && x.Key1 == row.Key1,
        ];

        foreach (var match in plannable)
        {
            var plan = EntityRowMatchPlan.TryCreate(match, entityType, match.Parameters[0], match.Parameters[1]);
            Assert.True(plan is not null, $"Expected a plan for '{match}'.");
        }

        // decimal lives on Order, not Item - confirm it is plannable there too.
        var orderType = context.Model.FindEntityType(typeof(Order))!;
        Expression<Func<Order, Order, bool>> decimalMatch = (row, x) => x.Amount == row.Amount;
        Assert.True(EntityRowMatchPlan.TryCreate(decimalMatch, orderType, decimalMatch.Parameters[0], decimalMatch.Parameters[1]) is not null);
    }

    [Fact]
    public void Unsupported_shapes_are_rejected_so_the_translator_still_handles_them()
    {
        using var context = CreateContext();
        var entityType = ItemType(context);

        // Each of these must yield null (=> per-row fallback), never a wrong plan.
        var rejected = new Expression<Func<Item, Item, bool>>[]
        {
            // No row reference at all.
            (row, x) => x.Id == 5,
            // Both sides entity-side.
            (row, x) => x.Id == x.Key2,
            // Both sides row-side.
            (row, x) => row.Id == row.Key2,
            // Row side is not a direct member (nested path).
            (row, x) => x.Id == row.NotMapped.Length,
            // Not a comparison.
            (row, x) => !x.Active,
            // Method call on an entity member.
            (row, x) => x.Key1.StartsWith(row.Key1),
            // Method call on the row side.
            (row, x) => x.Key1 == row.Key1.Substring(0, 1),
            // One conjunct plannable, the other not: the whole plan must decline so the
            // And/Or nesting is never half-built.
            (row, x) => x.Id == row.Id && x.Key1.StartsWith(row.Key1),
        };

        foreach (var match in rejected)
        {
            var plan = EntityRowMatchPlan.TryCreate(match, entityType, match.Parameters[0], match.Parameters[1]);
            Assert.True(plan is null, $"Expected no plan for '{match}' but one was produced.");
        }
    }

    [Fact]
    public void Unmapped_member_rejects_the_plan_instead_of_producing_a_wrong_one()
    {
        using var context = CreateContext();
        var entityType = ItemType(context);

        // NotMapped is Ignored in the model, so the plan must decline and the fallback must
        // surface the translator's own "not part of the EF model" error.
        Expression<Func<Item, Item, bool>> match = (row, x) => x.NotMapped == row.NotMapped;
        var plan = EntityRowMatchPlan.TryCreate(match, entityType, match.Parameters[0], match.Parameters[1]);
        Assert.True(plan is null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ReferenceTranslate(match, entityType, new Item { NotMapped = "q" }));
        Assert.Contains("NotMapped", ex.Message);
    }
}
