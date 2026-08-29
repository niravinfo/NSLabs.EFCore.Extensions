using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class SetExpressionTranslator
{
    public static SqlNode Translate(LambdaExpression valueExpression, IEntityType entityType, ParameterExpression entityParameter)
    {
        ArgumentNullException.ThrowIfNull(valueExpression);
        ArgumentNullException.ThrowIfNull(entityParameter);
        return TranslateNode(valueExpression.Body, entityType, entityParameter);
    }

    private static SqlNode TranslateNode(Expression node, IEntityType entityType, ParameterExpression entityParameter)
        => node switch
        {
            ConstantExpression constant => new SqlParameterNode(NormalizeParamValue(constant.Value)),
            MemberExpression member => TranslateMember(member, entityType, entityParameter),
            UnaryExpression unary => TranslateUnary(unary, entityType, entityParameter),
            BinaryExpression binary => TranslateBinary(binary, entityType, entityParameter),
            _ when !ReferencesEntity(node, entityParameter) => new SqlParameterNode(NormalizeParamValue(Evaluate(node))),
            _ => throw new NotSupportedException(
                $"Computed SET expression node '{node.NodeType}' is not supported. Node: '{node}'. Supported: column references (x.Prop), captured variables, arithmetic (+, -, *, /, %), unary minus, and numeric conversions.")
        };

    private static SqlNode TranslateMember(MemberExpression member, IEntityType entityType, ParameterExpression entityParameter)
    {
        if (member.Expression == entityParameter)
        {
            var property = ResolveProperty(member, entityType);
            return new SqlColumnNode(property);
        }

        // Captured closure or static member without entity reference -> parameter
        if (!ReferencesEntity(member, entityParameter))
        {
            var value = NormalizeParamValue(Evaluate(member));
            return new SqlParameterNode(value);
        }

        // Member of a column? e.g. x.Price.ToString() would be method call not here.
        // If member is something like x.Price where x is entity but with Convert wrapper handled elsewhere,
        // the direct member case already handled. Anything else is unsupported.
        throw new NotSupportedException(
            $"Member access '{member}' is not supported in computed SET expressions. Only direct entity property access (x.Prop) and captured variables are supported.");
    }

    private static SqlNode TranslateUnary(UnaryExpression unary, IEntityType entityType, ParameterExpression entityParameter)
    {
        switch (unary.NodeType)
        {
            case ExpressionType.Convert:
            case ExpressionType.ConvertChecked:
            case ExpressionType.TypeAs:
                // If converting a column reference, preserve conversion flag (like predicate does)
                if (unary.Operand is MemberExpression { Expression: ParameterExpression } member
                    && member.Expression == entityParameter)
                {
                    return new SqlColumnNode(ResolveProperty(member, entityType)) { ConvertedInTree = true };
                }

                // Otherwise unwrap numeric widening / enum conversion
                return TranslateNode(unary.Operand, entityType, entityParameter);

            case ExpressionType.Negate:
            case ExpressionType.NegateChecked:
                var inner = TranslateNode(unary.Operand, entityType, entityParameter);
                return new SqlUnaryNode(SqlUnaryOperator.Negate, inner);

            case ExpressionType.UnaryPlus:
                return TranslateNode(unary.Operand, entityType, entityParameter);

            case ExpressionType.Not:
                throw new NotSupportedException(
                    $"Unary operator '{unary.NodeType}' is not supported in computed SET expressions. Only unary minus '-' is supported.");

            default:
                if (!ReferencesEntity(unary, entityParameter))
                {
                    return new SqlParameterNode(NormalizeParamValue(Evaluate(unary)));
                }

                throw new NotSupportedException(
                    $"Unary operator '{unary.NodeType}' is not supported in computed SET expressions. Node: '{unary}'.");
        }
    }

    private static SqlNode TranslateBinary(BinaryExpression node, IEntityType entityType, ParameterExpression entityParameter)
    {
        // Check for non-entity subtree early: e.g. factor * 2 where factor is captured
        // That subtree will already be two params if neither references entity, but we can evaluate whole binary
        // as param. However plan says support Param [+ - * / %] Param via evaluate? We prefer to evaluate only if
        // no entity reference at all to keep consistent parameterization. If no entity reference, collapse to single param.
        if (!ReferencesEntity(node, entityParameter))
        {
            return new SqlParameterNode(NormalizeParamValue(Evaluate(node)));
        }

        var left = TranslateNode(node.Left, entityType, entityParameter);
        var right = TranslateNode(node.Right, entityType, entityParameter);

        var op = node.NodeType switch
        {
            ExpressionType.Add or ExpressionType.AddChecked => SqlBinaryOperator.Add,
            ExpressionType.Subtract or ExpressionType.SubtractChecked => SqlBinaryOperator.Subtract,
            ExpressionType.Multiply or ExpressionType.MultiplyChecked => SqlBinaryOperator.Multiply,
            ExpressionType.Divide => SqlBinaryOperator.Divide,
            ExpressionType.Modulo => SqlBinaryOperator.Modulo,
            _ => (SqlBinaryOperator?)null
        };

        if (op is not null)
        {
            // Apply value-converter to parameter side only when parameter type matches column's CLR type (like predicate does for enum)
            if (left is SqlColumnNode leftColumn && leftColumn.ConvertedInTree && right is SqlParameterNode rightParam && rightParam.Value is not null
                && leftColumn.Property.ClrType.IsInstanceOfType(rightParam.Value))
            {
                right = new SqlParameterNode(ModelBinder.ConvertToProvider(leftColumn.Property, rightParam.Value));
            }
            else if (right is SqlColumnNode rightColumn && rightColumn.ConvertedInTree && left is SqlParameterNode leftParam && leftParam.Value is not null
                     && rightColumn.Property.ClrType.IsInstanceOfType(leftParam.Value))
            {
                left = new SqlParameterNode(ModelBinder.ConvertToProvider(rightColumn.Property, leftParam.Value));
            }

            return new SqlBinaryNode(op.Value, left, right);
        }

        throw new NotSupportedException(
            $"Binary operator '{node.NodeType}' is not supported in computed SET expressions. Supported operators: Add (+), Subtract (-), Multiply (*), Divide (/), Modulo (%). Node: '{node}'.");
    }

    private static bool ReferencesEntity(Expression node, ParameterExpression entityParameter)
        => node switch
        {
            ParameterExpression parameter => ReferenceEquals(parameter, entityParameter),
            MemberExpression { Expression: not null } member => ReferencesEntity(member.Expression, entityParameter),
            UnaryExpression unary => ReferencesEntity(unary.Operand, entityParameter),
            BinaryExpression binary => ReferencesEntity(binary.Left, entityParameter)
                                       || ReferencesEntity(binary.Right, entityParameter),
            MethodCallExpression call => call.Object is { } target && ReferencesEntity(target, entityParameter)
                                         || call.Arguments.Any(argument => ReferencesEntity(argument, entityParameter)),
            InvocationExpression invocation => ReferencesEntity(invocation.Expression, entityParameter)
                                               || invocation.Arguments.Any(argument => ReferencesEntity(argument, entityParameter)),
            ConditionalExpression conditional => ReferencesEntity(conditional.Test, entityParameter)
                                                 || ReferencesEntity(conditional.IfTrue, entityParameter)
                                                 || ReferencesEntity(conditional.IfFalse, entityParameter),
            NewArrayExpression newArray => newArray.Expressions.Any(expression => ReferencesEntity(expression, entityParameter)),
            _ => false
        };

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

    private static object? NormalizeParamValue(object? value)
        => value is Enum enumValue ? Convert.ChangeType(enumValue, Enum.GetUnderlyingType(enumValue.GetType())) : value;
}
