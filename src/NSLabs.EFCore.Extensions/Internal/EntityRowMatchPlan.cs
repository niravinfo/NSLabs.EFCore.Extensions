using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

/// <summary>
/// Precompiled plan for an entity-row match expression (<c>(row, x) =&gt; ...</c>).
/// </summary>
/// <remarks>
/// Binding an entity-row batch used to rewrite the match lambda <em>per row</em>:
/// an <see cref="ExpressionVisitor"/> traversal, a fresh constant, a fresh lambda, then a
/// full <see cref="LinqPredicateTranslator"/> pass that re-resolved every mapped property
/// through EF's model via <c>FindProperty</c> — for every row. The predicate's shape is
/// identical for every row, so all of that is hoisted here and computed once per batch:
/// properties are resolved to <see cref="IProperty"/> and the And/Or tree is fixed. Per row
/// only the values are read and wrapped, which is where the remaining cost lives.
/// </remarks>
/// <remarks>
/// <b>Correctness contract.</b> <see cref="TryCreate"/> returns <see langword="null"/> for
/// every shape it does not fully model, and the caller then falls back to the original
/// per-row rewrite. An unrecognised — or invalid — predicate therefore still produces
/// exactly what <see cref="LinqPredicateTranslator"/> produces, including its exceptions:
/// the plan never has to reproduce a diagnostic, only a successful translation.
/// EntityRowMatchPlanTests pins this by differentially comparing plan output
/// against the translator across a matrix of shapes and row values.
/// </remarks>
internal sealed class EntityRowMatchPlan
{
    private readonly Node _root;

    private EntityRowMatchPlan(Node root) => _root = root;

    /// <summary>
    /// Builds a plan for <paramref name="match"/>, or returns <see langword="null"/> when
    /// the expression is not a pure tree of row-vs-entity comparisons over direct members.
    /// </summary>
    public static EntityRowMatchPlan? TryCreate(
        LambdaExpression match,
        IEntityType entityType,
        ParameterExpression rowParameter,
        ParameterExpression entityParameter)
    {
        var root = TryBuild(match.Body, entityType, rowParameter, entityParameter);
        return root is null ? null : new EntityRowMatchPlan(root);
    }

    /// <summary>Produces the predicate node for one row.</summary>
    public SqlNode Build(object row) => _root.Build(row);

    private static Node? TryBuild(
        Expression body,
        IEntityType entityType,
        ParameterExpression rowParameter,
        ParameterExpression entityParameter)
    {
        if (body is not BinaryExpression binary)
        {
            return null;
        }

        switch (binary.NodeType)
        {
            case ExpressionType.AndAlso:
            case ExpressionType.OrElse:
            {
                var op = binary.NodeType == ExpressionType.AndAlso
                    ? SqlBinaryOperator.And
                    : SqlBinaryOperator.Or;

                // Both sides must be plannable: a partially-planned tree would change the
                // And/Or nesting, so anything unsupported rejects the whole plan.
                var left = TryBuild(binary.Left, entityType, rowParameter, entityParameter);
                if (left is null)
                {
                    return null;
                }

                var right = TryBuild(binary.Right, entityType, rowParameter, entityParameter);
                return right is null ? null : new LogicalNode(op, left, right);
            }

            case ExpressionType.Equal:
            case ExpressionType.NotEqual:
            case ExpressionType.LessThan:
            case ExpressionType.LessThanOrEqual:
            case ExpressionType.GreaterThan:
            case ExpressionType.GreaterThanOrEqual:
                return TryBuildComparison(binary, entityType, rowParameter, entityParameter);

            default:
                return null;
        }
    }

    private static Node? TryBuildComparison(
        BinaryExpression binary,
        IEntityType entityType,
        ParameterExpression rowParameter,
        ParameterExpression entityParameter)
    {
        // Exactly one side must be a direct member on the entity parameter, and the other a
        // direct member on the *row* parameter. Between them these reject: both-sides-entity
        // (nothing row-dependent), both-sides-row, and any side that is neither a direct
        // entity member nor a direct row member (constant, captured variable, method call,
        // nested path, or a Convert wrapper).
        var leftOnEntity = IsDirectMemberOf(binary.Left, entityParameter);
        var rightOnEntity = IsDirectMemberOf(binary.Right, entityParameter);
        if (leftOnEntity == rightOnEntity)
        {
            return null;
        }

        var columnMember = leftOnEntity ? binary.Left : binary.Right;
        var rowMember = leftOnEntity ? binary.Right : binary.Left;
        if (!IsDirectMemberOf(rowMember, rowParameter))
        {
            return null;
        }

        // Unmapped members resolve to null here and are rejected, so the translator raises
        // its own "not part of the EF model" error from the fallback path unchanged.
        var columnProperty = FindProperty(entityType, columnMember);
        var rowProperty = FindProperty(entityType, rowMember);
        if (columnProperty is null || rowProperty is null)
        {
            return null;
        }

        return new ComparisonNode(
            LinqPredicateTranslator.ToOperator(binary.NodeType),
            columnProperty,
            rowProperty,
            columnOnLeft: leftOnEntity);
    }

    private static bool IsDirectMemberOf(Expression expression, ParameterExpression parameter)
        => expression is MemberExpression member && ReferenceEquals(member.Expression, parameter);

    private static IProperty? FindProperty(IEntityType entityType, Expression member)
    {
        var memberInfo = ((MemberExpression)member).Member;
        return entityType.FindProperty(memberInfo) ?? entityType.FindProperty(memberInfo.Name);
    }

    private abstract class Node
    {
        public abstract SqlNode Build(object row);
    }

    private sealed class LogicalNode(SqlBinaryOperator op, Node left, Node right) : Node
    {
        public override SqlNode Build(object row)
            // No AsBooleanPredicate wrapper: the translator applies it to And/Or operands,
            // but those are comparisons or further logical nodes here, never bare columns.
            => new SqlBinaryNode(op, left.Build(row), right.Build(row));
    }

    private sealed class ComparisonNode(
        SqlBinaryOperator op,
        IProperty column,
        IProperty rowProperty,
        bool columnOnLeft) : Node
    {
        // Equal/NotEqual rewrite an equality against null into an IS [NOT] NULL test;
        // relational operators keep the raw parameter. That branch depends on the row's
        // value, so it is the one thing that cannot be hoisted out of the per-row work.
        private readonly bool _equalityFamily =
            op is SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual;

        public override SqlNode Build(object row)
        {
            var value = ModelBinder.ReadMemberValue(rowProperty, row);
            var columnNode = new SqlColumnNode(column);

            // ConvertedInTree is always false here: it is set only by
            // LinqPredicateTranslator.TranslateConverted for a Convert wrapper, and
            // TryBuildComparison requires the member to sit directly on the parameter.
            if (_equalityFamily)
            {
                if (value is null)
                {
                    return new SqlNullCheckNode(column, isNotNull: op == SqlBinaryOperator.NotEqual);
                }

                return Wrap(op, columnNode, ModelBinder.ConvertToProvider(column, value));
            }

            // Mirrors the translator's relational branch: convert only a non-null value.
            var operand = value is not null ? ModelBinder.ConvertToProvider(column, value) : value;
            return Wrap(op, columnNode, operand);
        }

        // Operand order is the user's, because the translator preserves it and the emitted
        // parameter order follows the tree.
        private SqlNode Wrap(SqlBinaryOperator op, SqlColumnNode columnNode, object? value)
        {
            var parameter = new SqlParameterNode(value);
            return columnOnLeft
                ? new SqlBinaryNode(op, columnNode, parameter)
                : new SqlBinaryNode(op, parameter, columnNode);
        }
    }
}
