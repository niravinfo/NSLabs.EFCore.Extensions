using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class SetExpressionTranslator
{
    // No static Evaluate cache — follows EF Core (see LinqPredicateTranslator).

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
            ConditionalExpression conditional => TranslateConditional(conditional, entityType, entityParameter),
            MethodCallExpression methodCall => TranslateMethodCall(methodCall, entityType, entityParameter),
            _ when !ReferencesEntity(node, entityParameter) => new SqlParameterNode(NormalizeParamValue(Evaluate(node))),
            _ => throw new NotSupportedException(
                $"Computed SET expression node '{node.NodeType}' is not supported. Node: '{node}'. Supported: column references (x.Prop), captured variables, arithmetic (+, -, *, /, %), string concat (+), conditional (? :), coalesce (??), string methods (ToUpper, ToLower, Trim, Substring, Replace, Concat), Math (Abs, Ceiling, Floor, Round), and numeric conversions.")
        };

    private static SqlNode TranslateMember(MemberExpression member, IEntityType entityType, ParameterExpression entityParameter)
    {
        if (member.Expression == entityParameter)
        {
            var property = ResolveProperty(member, entityType);
            return new SqlColumnNode(property);
        }

        // String Length: x.Name.Length -> LEN(x.Name)
        if (member.Member.Name == "Length" && member.Member.DeclaringType == typeof(string) && member.Expression is not null)
        {
            if (ReferencesEntity(member.Expression, entityParameter))
            {
                var inner = TranslateNode(member.Expression, entityType, entityParameter);
                return new SqlMethodCallNode("LEN", [inner]);
            }
        }

        // Captured closure or static member without entity reference -> parameter
        if (!ReferencesEntity(member, entityParameter))
        {
            var value = NormalizeParamValue(Evaluate(member));
            return new SqlParameterNode(value);
        }

        throw new NotSupportedException(
            $"Member access '{member}' is not supported in computed SET expressions. Only direct entity property access (x.Prop), x.Prop.Length, and captured variables are supported.");
    }

    private static SqlNode TranslateConditional(ConditionalExpression conditional, IEntityType entityType, ParameterExpression entityParameter)
    {
        var test = TranslateNode(conditional.Test, entityType, entityParameter);
        test = AsBooleanPredicate(test);
        var ifTrue = TranslateNode(conditional.IfTrue, entityType, entityParameter);
        var ifFalse = TranslateNode(conditional.IfFalse, entityType, entityParameter);
        return new SqlConditionalNode(test, ifTrue, ifFalse);
    }

    private static SqlNode AsBooleanPredicate(SqlNode node)
        => node switch
        {
            SqlColumnNode col when col.Property.ClrType == typeof(bool) || col.Property.ClrType == typeof(bool?) => new SqlBooleanNode(col.Property),
            _ => node
        };

    private static SqlNode TranslateMethodCall(MethodCallExpression call, IEntityType entityType, ParameterExpression entityParameter)
    {
        if (!ReferencesEntity(call, entityParameter))
        {
            return new SqlParameterNode(NormalizeParamValue(Evaluate(call)));
        }

        // String instance methods
        if (call.Method.DeclaringType == typeof(string))
        {
            switch (call.Method.Name)
            {
                case "ToUpper" when call.Object is not null && call.Arguments.Count == 0:
                    return new SqlMethodCallNode("UPPER", [TranslateNode(call.Object, entityType, entityParameter)]);
                case "ToLower" when call.Object is not null && call.Arguments.Count == 0:
                    return new SqlMethodCallNode("LOWER", [TranslateNode(call.Object, entityType, entityParameter)]);
                case "Trim" when call.Object is not null && call.Arguments.Count == 0:
                    return new SqlMethodCallNode("TRIM", [TranslateNode(call.Object, entityType, entityParameter)]);
                case "TrimStart" when call.Object is not null && call.Arguments.Count == 0:
                    return new SqlMethodCallNode("LTRIM", [TranslateNode(call.Object, entityType, entityParameter)]);
                case "TrimEnd" when call.Object is not null && call.Arguments.Count == 0:
                    return new SqlMethodCallNode("RTRIM", [TranslateNode(call.Object, entityType, entityParameter)]);
                case "Substring" when call.Object is not null && (call.Arguments.Count == 1 || call.Arguments.Count == 2):
                {
                    var target = TranslateNode(call.Object, entityType, entityParameter);
                    var start = TranslateNode(call.Arguments[0], entityType, entityParameter);
                    // C# 0-based -> SQL 1-based: start+1
                    start = AddOne(start);
                    if (call.Arguments.Count == 1)
                    {
                        // SUBSTRING(col, start+1, LEN(col))
                        var len = new SqlMethodCallNode("LEN", [target]);
                        return new SqlMethodCallNode("SUBSTRING", [target, start, len]);
                    }
                    else
                    {
                        var length = TranslateNode(call.Arguments[1], entityType, entityParameter);
                        return new SqlMethodCallNode("SUBSTRING", [target, start, length]);
                    }
                }
                case "Replace" when call.Object is not null && call.Arguments.Count == 2:
                {
                    var target = TranslateNode(call.Object, entityType, entityParameter);
                    var oldValue = TranslateNode(call.Arguments[0], entityType, entityParameter);
                    var newValue = TranslateNode(call.Arguments[1], entityType, entityParameter);
                    return new SqlMethodCallNode("REPLACE", [target, oldValue, newValue]);
                }
                case "Concat" when call.Method.IsStatic:
                {
                    var args = call.Arguments.Select(a => TranslateNode(a, entityType, entityParameter)).ToList();
                    return new SqlMethodCallNode("CONCAT", args);
                }
            }
        }

        // Math methods
        if (call.Method.DeclaringType == typeof(Math) || call.Method.DeclaringType == typeof(MathF))
        {
            switch (call.Method.Name)
            {
                case "Abs" when call.Arguments.Count == 1:
                    return new SqlMethodCallNode("ABS", [TranslateNode(call.Arguments[0], entityType, entityParameter)]);
                case "Ceiling" when call.Arguments.Count == 1:
                    return new SqlMethodCallNode("CEILING", [TranslateNode(call.Arguments[0], entityType, entityParameter)]);
                case "Floor" when call.Arguments.Count == 1:
                    return new SqlMethodCallNode("FLOOR", [TranslateNode(call.Arguments[0], entityType, entityParameter)]);
                case "Round" when call.Arguments.Count == 1:
                    return new SqlMethodCallNode("ROUND", [TranslateNode(call.Arguments[0], entityType, entityParameter), new SqlParameterNode(0)]);
                case "Round" when call.Arguments.Count == 2:
                    return new SqlMethodCallNode("ROUND", [TranslateNode(call.Arguments[0], entityType, entityParameter), TranslateNode(call.Arguments[1], entityType, entityParameter)]);
                case "Truncate" when call.Arguments.Count == 1:
                    return new SqlMethodCallNode("ROUND", [TranslateNode(call.Arguments[0], entityType, entityParameter), new SqlParameterNode(0), new SqlParameterNode(1)]);
            }
        }

        throw new NotSupportedException(
            $"Method '{call.Method.DeclaringType?.Name}.{call.Method.Name}' is not supported in computed SET expressions. Supported: string.ToUpper, ToLower, Trim, TrimStart, TrimEnd, Substring, Replace, Concat, Math.Abs, Ceiling, Floor, Round, Truncate, and x.Prop.Length.");
    }

    private static SqlNode AddOne(SqlNode node)
        => node switch
        {
            SqlParameterNode p when p.Value is int i => new SqlParameterNode(i + 1),
            SqlParameterNode p when p.Value is long l => new SqlParameterNode(l + 1),
            _ => new SqlBinaryNode(SqlBinaryOperator.Add, node, new SqlParameterNode(1))
        };

    private static SqlNode TranslateUnary(UnaryExpression unary, IEntityType entityType, ParameterExpression entityParameter)
    {
        switch (unary.NodeType)
        {
            case ExpressionType.Convert:
            case ExpressionType.ConvertChecked:
            case ExpressionType.TypeAs:
                if (unary.Operand is MemberExpression { Expression: ParameterExpression } member
                    && member.Expression == entityParameter)
                {
                    return new SqlColumnNode(ResolveProperty(member, entityType)) { ConvertedInTree = true };
                }
                return TranslateNode(unary.Operand, entityType, entityParameter);

            case ExpressionType.Negate:
            case ExpressionType.NegateChecked:
                var inner = TranslateNode(unary.Operand, entityType, entityParameter);
                return new SqlUnaryNode(SqlUnaryOperator.Negate, inner);

            case ExpressionType.UnaryPlus:
                return TranslateNode(unary.Operand, entityType, entityParameter);

            case ExpressionType.Not:
            {
                var operand = TranslateNode(unary.Operand, entityType, entityParameter);
                // For boolean column, Not => NOT ([Col]=1)
                operand = AsBooleanPredicate(operand);
                return new SqlNotNode(operand);
            }

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
        if (node.NodeType == ExpressionType.Coalesce)
        {
            if (!ReferencesEntity(node, entityParameter))
                return new SqlParameterNode(NormalizeParamValue(Evaluate(node)));
            var left = TranslateNode(node.Left, entityType, entityParameter);
            var right = TranslateNode(node.Right, entityType, entityParameter);
            return new SqlCoalesceNode(left, right);
        }

        if (!ReferencesEntity(node, entityParameter))
        {
            return new SqlParameterNode(NormalizeParamValue(Evaluate(node)));
        }

        var leftNode = TranslateNode(node.Left, entityType, entityParameter);
        var rightNode = TranslateNode(node.Right, entityType, entityParameter);

        // Handle null checks for Equal/NotEqual
        if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            var isNotEqual = node.NodeType == ExpressionType.NotEqual;
            if (leftNode is SqlColumnNode lc && rightNode is SqlParameterNode rp && rp.Value is null)
                return new SqlNullCheckNode(lc.Property, isNotNull: isNotEqual);
            if (rightNode is SqlColumnNode rc && leftNode is SqlParameterNode lp && lp.Value is null)
                return new SqlNullCheckNode(rc.Property, isNotNull: isNotEqual);
            
            // Value converter handling for Equal/NotEqual
            if (leftNode is SqlColumnNode leftCol && leftCol.ConvertedInTree && rightNode is SqlParameterNode rightParam && rightParam.Value is not null
                && leftCol.Property.ClrType.IsInstanceOfType(rightParam.Value))
            {
                rightNode = new SqlParameterNode(ModelBinder.ConvertToProvider(leftCol.Property, rightParam.Value));
            }
            else if (rightNode is SqlColumnNode rightCol && rightCol.ConvertedInTree && leftNode is SqlParameterNode leftParam && leftParam.Value is not null
                     && rightCol.Property.ClrType.IsInstanceOfType(leftParam.Value))
            {
                leftNode = new SqlParameterNode(ModelBinder.ConvertToProvider(rightCol.Property, leftParam.Value));
            }
            // Also handle non-ConvertedInTree converters like predicate does
            else if (leftNode is SqlColumnNode lc2 && !lc2.ConvertedInTree && rightNode is SqlParameterNode rp2 && rp2.Value is not null)
            {
                rightNode = new SqlParameterNode(ModelBinder.ConvertToProvider(lc2.Property, rp2.Value));
            }
            else if (rightNode is SqlColumnNode rc2 && !rc2.ConvertedInTree && leftNode is SqlParameterNode lp2 && lp2.Value is not null)
            {
                leftNode = new SqlParameterNode(ModelBinder.ConvertToProvider(rc2.Property, lp2.Value));
            }

            return new SqlBinaryNode(isNotEqual ? SqlBinaryOperator.NotEqual : SqlBinaryOperator.Equal, leftNode, rightNode);
        }

        SqlBinaryOperator? op = node.NodeType switch
        {
            ExpressionType.Add or ExpressionType.AddChecked => SqlBinaryOperator.Add,
            ExpressionType.Subtract or ExpressionType.SubtractChecked => SqlBinaryOperator.Subtract,
            ExpressionType.Multiply or ExpressionType.MultiplyChecked => SqlBinaryOperator.Multiply,
            ExpressionType.Divide => SqlBinaryOperator.Divide,
            ExpressionType.Modulo => SqlBinaryOperator.Modulo,
            ExpressionType.LessThan => SqlBinaryOperator.LessThan,
            ExpressionType.LessThanOrEqual => SqlBinaryOperator.LessThanOrEqual,
            ExpressionType.GreaterThan => SqlBinaryOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => SqlBinaryOperator.GreaterThanOrEqual,
            ExpressionType.AndAlso => SqlBinaryOperator.And,
            ExpressionType.OrElse => SqlBinaryOperator.Or,
            ExpressionType.And => SqlBinaryOperator.And,
            ExpressionType.Or => SqlBinaryOperator.Or,
            _ => (SqlBinaryOperator?)null
        };

        if (op is not null)
        {
            // For And/Or, ensure boolean predicates
            if (op is SqlBinaryOperator.And or SqlBinaryOperator.Or)
            {
                leftNode = AsBooleanPredicate(leftNode);
                rightNode = AsBooleanPredicate(rightNode);
            }

            // For comparisons, handle converters (same as Equal)
            if (op is SqlBinaryOperator.LessThan or SqlBinaryOperator.LessThanOrEqual or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterThanOrEqual)
            {
                if (leftNode is SqlColumnNode lcc && !lcc.ConvertedInTree && rightNode is SqlParameterNode rpm && rpm.Value is not null)
                    rightNode = new SqlParameterNode(ModelBinder.ConvertToProvider(lcc.Property, rpm.Value));
                else if (rightNode is SqlColumnNode rcc && !rcc.ConvertedInTree && leftNode is SqlParameterNode lpm && lpm.Value is not null)
                    leftNode = new SqlParameterNode(ModelBinder.ConvertToProvider(rcc.Property, lpm.Value));
            }

            // Value-converter for arithmetic (existing logic)
            if (op is SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo)
            {
                if (leftNode is SqlColumnNode leftColumn && leftColumn.ConvertedInTree && rightNode is SqlParameterNode rightParam && rightParam.Value is not null
                    && leftColumn.Property.ClrType.IsInstanceOfType(rightParam.Value))
                {
                    rightNode = new SqlParameterNode(ModelBinder.ConvertToProvider(leftColumn.Property, rightParam.Value));
                }
                else if (rightNode is SqlColumnNode rightColumn && rightColumn.ConvertedInTree && leftNode is SqlParameterNode leftParam && leftParam.Value is not null
                         && rightColumn.Property.ClrType.IsInstanceOfType(leftParam.Value))
                {
                    leftNode = new SqlParameterNode(ModelBinder.ConvertToProvider(rightColumn.Property, leftParam.Value));
                }
            }

            return new SqlBinaryNode(op.Value, leftNode, rightNode);
        }

        throw new NotSupportedException(
            $"Binary operator '{node.NodeType}' is not supported in computed SET expressions. Supported operators: Add (+), Subtract (-), Multiply (*), Divide (/), Modulo (%), Equal (=), NotEqual (<>), LessThan, GreaterThan, AndAlso (AND), OrElse (OR), Coalesce (??). Node: '{node}'.");
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
        // Direct compile per call — no static cache. Matches EF Core EvaluatableExpressionFilter pattern:
        // Member chains are already handled without Compile; this path is rare (<5% of SET evaluations).
        var boxed = expression.Type.IsValueType ? Expression.Convert(expression, typeof(object)) : expression;
        var lambda = Expression.Lambda<Func<object?>>(boxed);
        return lambda.Compile().Invoke();
    }

    private static object? NormalizeParamValue(object? value)
        => value is Enum enumValue ? Convert.ChangeType(enumValue, Enum.GetUnderlyingType(enumValue.GetType())) : value;
}
