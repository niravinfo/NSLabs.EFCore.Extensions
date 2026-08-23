using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class LinqPredicateTranslator
{
    public static SqlNode Translate(LambdaExpression predicate, IEntityType entityType, ParameterExpression entityParameter)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(entityParameter);
        return AsBooleanPredicate(Translate(predicate.Body, entityType, entityParameter));
    }

    private static SqlNode Translate(Expression node, IEntityType entityType, ParameterExpression entityParameter)
        => node switch
        {
            ConstantExpression constant => new SqlParameterNode(constant.Value),
            MemberExpression member => TranslateMember(member, entityType, entityParameter),
            UnaryExpression { NodeType: ExpressionType.Not } not =>
                new SqlNotNode(AsBooleanPredicate(Translate(not.Operand, entityType, entityParameter))),
            UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs
            } convert => TranslateConverted(convert, entityType, entityParameter),
            BinaryExpression binary => TranslateBinary(binary, entityType, entityParameter),
            _ => throw new NotSupportedException($"Predicate construct '{node.NodeType}' is not supported. Node: '{node}'.")
        };

    private static SqlNode TranslateConverted(UnaryExpression convert, IEntityType entityType, ParameterExpression entityParameter)
    {
        if (convert.Operand is MemberExpression { Expression: ParameterExpression } member
            && member.Expression == entityParameter)
        {
            return new SqlColumnNode(ResolveProperty(member, entityType)) { ConvertedInTree = true };
        }

        return Translate(convert.Operand, entityType, entityParameter);
    }

    private static SqlNode TranslateBinary(BinaryExpression node, IEntityType entityType, ParameterExpression entityParameter)
    {
        var left = Translate(node.Left, entityType, entityParameter);
        var right = Translate(node.Right, entityType, entityParameter);

        switch (node.NodeType)
        {
            case ExpressionType.Equal:
            case ExpressionType.NotEqual:
                var isNotEqual = node.NodeType == ExpressionType.NotEqual;

                if (left is SqlColumnNode leftColumn && right is SqlParameterNode rightParam)
                {
                    if (rightParam.Value is null)
                    {
                        return new SqlNullCheckNode(leftColumn.Property, isNotNull: isNotEqual);
                    }

                    var convertedValue = leftColumn.ConvertedInTree
                        ? rightParam.Value
                        : ModelBinder.ConvertToProvider(leftColumn.Property, rightParam.Value);

                    return new SqlBinaryNode(
                        ToOperator(node.NodeType),
                        left,
                        new SqlParameterNode(convertedValue));
                }

                if (right is SqlColumnNode rightColumn && left is SqlParameterNode leftParam)
                {
                    if (leftParam.Value is null)
                    {
                        return new SqlNullCheckNode(rightColumn.Property, isNotNull: isNotEqual);
                    }

                    var convertedValue = rightColumn.ConvertedInTree
                        ? leftParam.Value
                        : ModelBinder.ConvertToProvider(rightColumn.Property, leftParam.Value);

                    return new SqlBinaryNode(
                        ToOperator(node.NodeType),
                        new SqlParameterNode(convertedValue),
                        right);
                }

                return new SqlBinaryNode(ToOperator(node.NodeType), left, right);

            case ExpressionType.LessThan:
            case ExpressionType.LessThanOrEqual:
            case ExpressionType.GreaterThan:
            case ExpressionType.GreaterThanOrEqual:
                if (left is SqlColumnNode ltColumn && !ltColumn.ConvertedInTree
                    && right is SqlParameterNode rtParam && rtParam.Value is not null)
                {
                    right = new SqlParameterNode(ModelBinder.ConvertToProvider(ltColumn.Property, rtParam.Value));
                }
                else if (right is SqlColumnNode rtColumn && !rtColumn.ConvertedInTree
                         && left is SqlParameterNode lfParam && lfParam.Value is not null)
                {
                    left = new SqlParameterNode(ModelBinder.ConvertToProvider(rtColumn.Property, lfParam.Value));
                }

                return new SqlBinaryNode(ToOperator(node.NodeType), left, right);

            case ExpressionType.AndAlso:
                return new SqlBinaryNode(SqlBinaryOperator.And, AsBooleanPredicate(left), AsBooleanPredicate(right));

            case ExpressionType.OrElse:
                return new SqlBinaryNode(SqlBinaryOperator.Or, AsBooleanPredicate(left), AsBooleanPredicate(right));

            default:
                throw new NotSupportedException($"Binary operator '{node.NodeType}' is not supported in predicates.");
        }
    }

    private static SqlNode AsBooleanPredicate(SqlNode node)
        => node is SqlColumnNode column ? new SqlBooleanNode(column.Property) : node;

    private static SqlNode TranslateMember(MemberExpression member, IEntityType entityType, ParameterExpression entityParameter)
    {
        if (member.Expression == entityParameter)
        {
            var property = ResolveProperty(member, entityType);
            return new SqlColumnNode(property);
        }

        if (member.Expression is ConstantExpression or MemberExpression or UnaryExpression)
        {
            return new SqlParameterNode(Evaluate(member));
        }

        throw new NotSupportedException($"Member access on '{member.Expression?.NodeType}' is not supported in predicates.");
    }

    private static IProperty ResolveProperty(MemberExpression member, IEntityType entityType)
        => entityType.FindProperty(member.Member)
           ?? entityType.FindProperty(member.Member.Name)
           ?? throw new InvalidOperationException(
               $"Property '{entityType.DisplayName()}.{member.Member.Name}' is not part of the EF model and cannot be used in bulk operations.");

    private static object? Evaluate(Expression expression)
    {
        var boxed = expression.Type.IsValueType ? Expression.Convert(expression, typeof(object)) : expression;
        var lambda = Expression.Lambda<Func<object?>>(boxed);
        return lambda.Compile()();
    }

    private static SqlBinaryOperator ToOperator(ExpressionType type) => type switch
    {
        ExpressionType.Equal => SqlBinaryOperator.Equal,
        ExpressionType.NotEqual => SqlBinaryOperator.NotEqual,
        ExpressionType.LessThan => SqlBinaryOperator.LessThan,
        ExpressionType.LessThanOrEqual => SqlBinaryOperator.LessThanOrEqual,
        ExpressionType.GreaterThan => SqlBinaryOperator.GreaterThan,
        ExpressionType.GreaterThanOrEqual => SqlBinaryOperator.GreaterThanOrEqual,
        _ => throw new NotSupportedException($"Operator '{type}' is not supported.")
    };
}
