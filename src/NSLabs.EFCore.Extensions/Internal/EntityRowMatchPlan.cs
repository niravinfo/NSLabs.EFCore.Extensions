using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

/// <summary>
/// A reusable, bind-time plan for the common entity-row match shape:
/// a direct equality comparison between an entity property and the same property on the input row,
/// optionally composed with <see cref="ExpressionType.AndAlso"/>.
/// </summary>
internal sealed class EntityRowMatchPlan
{
    private readonly Node _root;

    private EntityRowMatchPlan(Node root) => _root = root;

    public static EntityRowMatchPlan? TryCreate(LambdaExpression match, IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(entityType);

        if (match.Parameters.Count != 2)
        {
            return null;
        }

        var root = TryCreateNode(match.Body, match.Parameters[0], match.Parameters[1], entityType);
        return root is null ? null : new EntityRowMatchPlan(root);
    }

    public SqlNode Bind(object row) => _root.Bind(row);

    private static Node? TryCreateNode(
        Expression expression,
        ParameterExpression rowParameter,
        ParameterExpression entityParameter,
        IEntityType entityType)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType == ExpressionType.AndAlso)
            {
                var left = TryCreateNode(binary.Left, rowParameter, entityParameter, entityType);
                var right = TryCreateNode(binary.Right, rowParameter, entityParameter, entityType);
                return left is not null && right is not null ? new AndNode(left, right) : null;
            }

            if (binary.NodeType == ExpressionType.Equal)
            {
                return TryCreateComparison(binary, rowParameter, entityParameter, entityType);
            }
        }

        return null;
    }

    private static ComparisonNode? TryCreateComparison(
        BinaryExpression equality,
        ParameterExpression rowParameter,
        ParameterExpression entityParameter,
        IEntityType entityType)
    {
        var entityLeft = TryGetDirectMember(equality.Left, entityParameter);
        var rowRight = TryGetDirectMember(equality.Right, rowParameter);
        if (entityLeft is not null && rowRight is not null)
        {
            return TryCreateComparison(entityLeft, rowRight, entityType, entityColumnOnLeft: true);
        }

        var rowLeft = TryGetDirectMember(equality.Left, rowParameter);
        var entityRight = TryGetDirectMember(equality.Right, entityParameter);
        return rowLeft is not null && entityRight is not null
            ? TryCreateComparison(entityRight, rowLeft, entityType, entityColumnOnLeft: false)
            : null;
    }

    private static ComparisonNode? TryCreateComparison(
        MemberExpression entityMember,
        MemberExpression rowMember,
        IEntityType entityType,
        bool entityColumnOnLeft)
    {
        // Reading through EF metadata is safe only when both sides name the exact same CLR member.
        // Anything unusual (interface/field/property mismatches, conversions, cross-property matches)
        // stays on the fully general translator path.
        if (entityMember.Member != rowMember.Member)
        {
            return null;
        }

        var property = entityType.FindProperty(entityMember.Member)
            ?? entityType.FindProperty(entityMember.Member.Name);
        if (property is null)
        {
            return null;
        }

        return new ComparisonNode(property, entityColumnOnLeft);
    }

    private static MemberExpression? TryGetDirectMember(Expression expression, ParameterExpression parameter)
        => expression is MemberExpression { Expression: ParameterExpression root } member
           && ReferenceEquals(root, parameter)
            ? member
            : null;

    private abstract class Node
    {
        public abstract SqlNode Bind(object row);
    }

    private sealed class ComparisonNode(IProperty property, bool entityColumnOnLeft) : Node
    {
        private readonly SqlColumnNode _column = new(property);
        private readonly SqlNullCheckNode _nullCheck = new(property, isNotNull: false);

        public override SqlNode Bind(object row)
        {
            var rawValue = ModelBinder.ReadMemberValue(property, row);
            if (rawValue is null)
            {
                return _nullCheck;
            }

            var parameter = new SqlParameterNode(ModelBinder.ConvertToProvider(property, rawValue));
            return entityColumnOnLeft
                ? new SqlBinaryNode(SqlBinaryOperator.Equal, _column, parameter)
                : new SqlBinaryNode(SqlBinaryOperator.Equal, parameter, _column);
        }
    }

    private sealed class AndNode(Node left, Node right) : Node
    {
        public override SqlNode Bind(object row)
            => new SqlBinaryNode(SqlBinaryOperator.And, left.Bind(row), right.Bind(row));
    }
}
