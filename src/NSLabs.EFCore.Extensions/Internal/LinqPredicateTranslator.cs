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
            MethodCallExpression methodCall => TranslateMethodCall(methodCall, entityType, entityParameter),
            _ when !ReferencesEntity(node, entityParameter) =>
                new SqlParameterNode(Evaluate(node)),
            _ => throw new NotSupportedException($"Predicate construct '{node.NodeType}' is not supported. Node: '{node}'. Supported: == != < <= > >= && || !, string.Contains/StartsWith/EndsWith/Equals, string.IsNullOrEmpty, collection IN (list.Contains(x.Prop)), EF.Functions.Like.")
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
                if (!ReferencesEntity(node, entityParameter))
                {
                    return new SqlParameterNode(Evaluate(node));
                }

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

    private static SqlNode TranslateMethodCall(MethodCallExpression call, IEntityType entityType, ParameterExpression entityParameter)
    {
        // Non-entity calls -> evaluate as parameter (e.g., string.IsNullOrEmpty on captured variable)
        if (!ReferencesEntity(call, entityParameter))
        {
            return new SqlParameterNode(Evaluate(call));
        }

        // string instance: Contains / StartsWith / EndsWith / Equals
        if (call.Method.DeclaringType == typeof(string))
        {
            if (call.Object is MemberExpression { Expression: ParameterExpression } objMember && objMember.Expression == entityParameter)
            {
                var property = ResolveProperty(objMember, entityType);
                switch (call.Method.Name)
                {
                    case "Contains" when call.Arguments.Count == 1:
                    {
                        var pattern = Evaluate(call.Arguments[0]) as string;
                        if (pattern is null) throw new NotSupportedException("string.Contains with null pattern is not supported. Use == null check.");
                        return new SqlLikeNode(property, pattern, SqlLikeKind.Contains);
                    }
                    case "StartsWith" when call.Arguments.Count == 1:
                    {
                        var pattern = Evaluate(call.Arguments[0]) as string;
                        if (pattern is null) throw new NotSupportedException("string.StartsWith with null pattern is not supported.");
                        return new SqlLikeNode(property, pattern, SqlLikeKind.StartsWith);
                    }
                    case "EndsWith" when call.Arguments.Count == 1:
                    {
                        var pattern = Evaluate(call.Arguments[0]) as string;
                        if (pattern is null) throw new NotSupportedException("string.EndsWith with null pattern is not supported.");
                        return new SqlLikeNode(property, pattern, SqlLikeKind.EndsWith);
                    }
                    case "Equals" when call.Arguments.Count == 1:
                    {
                        var value = Evaluate(call.Arguments[0]);
                        var converted = ModelBinder.ConvertToProvider(property, value);
                        return new SqlBinaryNode(SqlBinaryOperator.Equal, new SqlColumnNode(property), new SqlParameterNode(converted));
                    }
                }
            }

            // static string.IsNullOrEmpty / IsNullOrWhiteSpace
            if (call.Method.IsStatic && call.Object is null)
            {
                switch (call.Method.Name)
                {
                    case "IsNullOrEmpty" when call.Arguments.Count == 1 && call.Arguments[0] is MemberExpression { Expression: ParameterExpression } m && m.Expression == entityParameter:
                    {
                        var prop = ResolveProperty(m, entityType);
                        return new SqlBinaryNode(SqlBinaryOperator.Or, new SqlNullCheckNode(prop, isNotNull: false), new SqlBinaryNode(SqlBinaryOperator.Equal, new SqlColumnNode(prop), new SqlParameterNode(""))) ;
                    }
                    case "IsNullOrWhiteSpace" when call.Arguments.Count == 1 && call.Arguments[0] is MemberExpression { Expression: ParameterExpression } m2 && m2.Expression == entityParameter:
                    {
                        var prop = ResolveProperty(m2, entityType);
                        // (col IS NULL OR LTRIM(RTRIM(col)) = '')
                        var trimmed = new SqlMethodCallNode("TRIM", [new SqlColumnNode(prop)]);
                        return new SqlBinaryNode(SqlBinaryOperator.Or, new SqlNullCheckNode(prop, isNotNull: false), new SqlBinaryNode(SqlBinaryOperator.Equal, trimmed, new SqlParameterNode("")));
                    }
                }
            }
        }

        // EF.Functions.Like extension: DbFunctionsExtensions.Like(this DbFunctions _, string match, string pattern)
        if (call.Method.Name == "Like" && call.Arguments.Count >= 2)
        {
            // Check if declaring type is DbFunctionsExtensions
            var decl = call.Method.DeclaringType?.FullName;
            if (decl is not null && decl.Contains("DbFunctionsExtensions"))
            {
                // Arguments: [DbFunctions, matchExpression, pattern]
                var matchArg = call.Arguments.Count == 3 ? call.Arguments[1] : call.Arguments[0];
                var patternArg = call.Arguments.Count == 3 ? call.Arguments[2] : call.Arguments[1];
                if (matchArg is MemberExpression { Expression: ParameterExpression } matchMember && matchMember.Expression == entityParameter)
                {
                    var prop = ResolveProperty(matchMember, entityType);
                    var pattern = Evaluate(patternArg) as string ?? throw new NotSupportedException("EF.Functions.Like pattern must be a constant string.");
                    return new SqlLikeNode(prop, pattern, SqlLikeKind.Like);
                }
            }
        }

        // Collection IN: list.Contains(entity.Prop) or Enumerable.Contains(list, entity.Prop) or MemoryExtensions.Contains(ReadOnlySpan, item)
        if (call.Method.Name == "Contains")
        {
            // Unwrap implicit conversion int[] -> ReadOnlySpan<int> for MemoryExtensions.Contains
            static Expression UnwrapImplicit(Expression expr)
            {
                if (expr is MethodCallExpression m && m.Method.Name == "op_Implicit" && m.Arguments.Count == 1)
                    return m.Arguments[0];
                return expr;
            }

            // Instance form: List<T>.Contains(T item) or HashSet<T>.Contains
            if (call.Object is not null && call.Arguments.Count == 1)
            {
                var collectionExpr = UnwrapImplicit(call.Object);
                var itemExpr = call.Arguments[0];
                if (!ReferencesEntity(collectionExpr, entityParameter) && ReferencesEntity(itemExpr, entityParameter) && UnwrapItemMember(itemExpr, entityParameter) is { } im)
                {
                    var prop = ResolveProperty(im, entityType);
                    var collection = Evaluate(collectionExpr) as System.Collections.IEnumerable;
                    if (collection is null) throw new NotSupportedException("Contains collection is null.");
                    var values = NewInValueList(prop, collection);
                    return new SqlInNode(prop, values);
                }
            }

            // Static form: Enumerable.Contains<T>(IEnumerable<T> source, T item),
            // MemoryExtensions.Contains<T>(ReadOnlySpan<T>, T), or its 3-arg overload
            // with an IEqualityComparer<T> (which the compiler picks for some element
            // types, e.g. enums — verified via expression dump).
            if (call.Object is null && (call.Arguments.Count == 2 || call.Arguments.Count == 3))
            {
                if (call.Arguments.Count == 3 && !IsDefaultEqualityComparer(call))
                {
                    throw new NotSupportedException(
                        "Contains with a custom IEqualityComparer<T> is not supported in predicates; SQL IN uses the database comparison semantics.");
                }

                var sourceExpr = UnwrapImplicit(call.Arguments[0]);
                var itemExpr = call.Arguments[1];
                if (!ReferencesEntity(sourceExpr, entityParameter) && ReferencesEntity(itemExpr, entityParameter) && UnwrapItemMember(itemExpr, entityParameter) is { } im2)
                {
                    var prop = ResolveProperty(im2, entityType);
                    var collection = Evaluate(sourceExpr) as System.Collections.IEnumerable;
                    if (collection is null) throw new NotSupportedException("Contains source is null.");
                    var values = NewInValueList(prop, collection);
                    return new SqlInNode(prop, values);
                }
            }
        }

        // Fallback: if method doesn't reference entity after all, evaluate
        if (!ReferencesEntity(call, entityParameter))
        {
            return new SqlParameterNode(Evaluate(call));
        }

        throw new NotSupportedException($"Method '{call.Method.DeclaringType?.Name}.{call.Method.Name}' is not supported in predicates. Supported: string.Contains/StartsWith/EndsWith/Equals, string.IsNullOrEmpty/IsNullOrWhiteSpace, collection.Contains (IN), EF.Functions.Like.");
    }

    // Materializes a Contains collection into the SqlInNode value list, converting each
    // element to its provider representation. The source is a non-generic IEnumerable, so
    // the element count is only known up front when it also implements ICollection
    // (arrays, List<T>, HashSet<T> — the overwhelmingly common shapes).
    private static List<object?> NewInValueList(IProperty property, System.Collections.IEnumerable collection)
    {
        var values = collection is System.Collections.ICollection sized
            ? new List<object?>(sized.Count)
            : [];

        foreach (var v in collection)
        {
            values.Add(ModelBinder.ConvertToProvider(property, v));
        }

        return values;
    }

    // The Contains item side is a bare member access in the common case, but a nullable
    // element type against a non-nullable column (List<int?> vs int PK) compiles to
    // Convert(x.Member) — a lifted no-op the translator must see through so the
    // §4.1 null-drop rule for non-nullable columns is reachable. The reverse
    // (List<int> vs int? member) does not compile, so no other shape can occur.
    private static MemberExpression? UnwrapItemMember(Expression expression, ParameterExpression entityParameter)
    {
        while (expression is UnaryExpression
               {
                   NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
               } convert)
        {
            expression = convert.Operand;
        }

        return expression is MemberExpression { Expression: ParameterExpression } member
               && ReferenceEquals(member.Expression, entityParameter)
            ? member
            : null;
    }

    // MemoryExtensions.Contains 3-arg form: only the default comparer preserves SQL
    // IN semantics. A null comparer means default semantics (supported); any other
    // instance cannot be honored in SQL and fails with a clear error above.
    private static bool IsDefaultEqualityComparer(MethodCallExpression call)
    {
        var comparer = Evaluate(call.Arguments[2]);
        if (comparer is null)
        {
            return true;
        }

        var elementType = call.Method.GetGenericArguments().FirstOrDefault();
        if (elementType is null)
        {
            return false;
        }

        return Equals(comparer, ExpressionEvaluatorCache.GetDefaultEqualityComparer(elementType));
    }

    private static string EscapeLike(string pattern)
    {
        // EF Core pattern: single-pass StringBuilderCache vs 3x Replace (3 string allocs) — for string.Contains/StartsWith/EndsWith LIKE
        // Fast-path: no special chars → return original (no alloc) — EF Core SearchValues pattern manual
        if (pattern.IndexOf('[') < 0 && pattern.IndexOf('%') < 0 && pattern.IndexOf('_') < 0)
        {
            return pattern;
        }

        var sb = StringBuilderCache.Acquire(pattern.Length + 8);
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '[') sb.Append("[[]");
            else if (c == '%') sb.Append("[%]");
            else if (c == '_') sb.Append("[_]");
            else sb.Append(c);
        }

        return StringBuilderCache.GetStringAndRelease(sb);
    }

    private static bool ReferencesEntity(Expression node, ParameterExpression entityParameter)
        => node switch
        {
            ParameterExpression parameter => ReferenceEquals(parameter, entityParameter),
            MemberExpression { Expression: not null } member => ReferencesEntity(member.Expression, entityParameter),
            UnaryExpression unary => ReferencesEntity(unary.Operand, entityParameter),
            BinaryExpression binary => ReferencesEntity(binary.Left, entityParameter)
                                       || ReferencesEntity(binary.Right, entityParameter),
            MethodCallExpression call => ReferencesEntityCall(call, entityParameter),
            InvocationExpression invocation => ReferencesEntityInvocation(invocation, entityParameter),
            ConditionalExpression conditional => ReferencesEntity(conditional.Test, entityParameter)
                                                 || ReferencesEntity(conditional.IfTrue, entityParameter)
                                                 || ReferencesEntity(conditional.IfFalse, entityParameter),
            NewArrayExpression newArray => AnyReferencesEntity(newArray.Expressions, entityParameter),
            _ => false
        };

    // EF Core pattern: manual loop vs LINQ Any(predicate) closure alloc — hot path per predicate node
    private static bool ReferencesEntityCall(MethodCallExpression call, ParameterExpression entityParameter)
    {
        if (call.Object is { } target && ReferencesEntity(target, entityParameter))
        {
            return true;
        }

        return AnyReferencesEntity(call.Arguments, entityParameter);
    }

    private static bool ReferencesEntityInvocation(InvocationExpression invocation, ParameterExpression entityParameter)
    {
        if (ReferencesEntity(invocation.Expression, entityParameter))
        {
            return true;
        }

        return AnyReferencesEntity(invocation.Arguments, entityParameter);
    }

    private static bool AnyReferencesEntity(IReadOnlyList<Expression> expressions, ParameterExpression entityParameter)
    {
        for (var i = 0; i < expressions.Count; i++)
        {
            if (ReferencesEntity(expressions[i], entityParameter))
            {
                return true;
            }
        }

        return false;
    }

    private static IProperty ResolveProperty(MemberExpression member, IEntityType entityType)
        => entityType.FindProperty(member.Member)
           ?? entityType.FindProperty(member.Member.Name)
           ?? throw new InvalidOperationException(
               $"Property '{entityType.DisplayName()}.{member.Member.Name}' is not part of the EF model and cannot be used in bulk operations.");

    private static object? Evaluate(Expression expression)
        => ExpressionEvaluatorCache.Evaluate(expression);

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
