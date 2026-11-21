using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class ExpressionEvaluatorCache
{
    private static readonly ConcurrentDictionary<MemberInfo, Func<object?, object?>> GetterCache = new();

    private static readonly ConcurrentDictionary<Type, object?> DefaultComparerCache = new();

    // Keys are stable shape metadata, never expression instances or captured values:
    // callers build a fresh expression tree for every bind.
    private static readonly ConcurrentDictionary<UnaryShape, Func<object?, object?>> UnaryCache = new();

    private static readonly ConcurrentDictionary<BinaryShape, Func<object?, object?, object?>> BinaryCache = new();

    private readonly record struct UnaryShape(
        ExpressionType Node, Type Operand, Type Result, MethodInfo? Method, bool IsLiftedToNull);

    private readonly record struct BinaryShape(
        ExpressionType Node, Type Left, Type Right, Type Result, MethodInfo? Method, bool IsLiftedToNull);

    public static object? Evaluate(Expression expression)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                return constant.Value;
            case MemberExpression member:
                return EvaluateMember(member);
            case UnaryExpression unary:
                return EvaluateUnary(unary);
            case BinaryExpression binary:
                return EvaluateBinary(binary);
            case NewArrayExpression newArray:
            {
                var elementType = newArray.Type.GetElementType() ?? typeof(object);
                var array = Array.CreateInstance(elementType, newArray.Expressions.Count);
                for (var i = 0; i < newArray.Expressions.Count; i++)
                {
                    array.SetValue(Evaluate(newArray.Expressions[i]), i);
                }

                return array;
            }
            case ListInitExpression listInit:
            {
                var list = Evaluate(listInit.NewExpression);
                if (list is System.Collections.IList ilist)
                {
                    foreach (var init in listInit.Initializers)
                    {
                        foreach (var arg in init.Arguments)
                        {
                            ilist.Add(Evaluate(arg));
                        }
                    }
                }

                return list;
            }
            case NewExpression newExpression:
            {
                if (newExpression.Constructor is null)
                {
                    return null;
                }

                var args = new object?[newExpression.Arguments.Count];
                for (var i = 0; i < newExpression.Arguments.Count; i++)
                {
                    args[i] = Evaluate(newExpression.Arguments[i]);
                }

                return newExpression.Constructor.Invoke(args);
            }
            default:
                return CompileAndEvaluate(expression);
        }
    }

    public static object? GetDefaultEqualityComparer(Type elementType)
        => DefaultComparerCache.GetOrAdd(elementType, static t =>
            typeof(EqualityComparer<>).MakeGenericType(t).GetProperty("Default")?.GetValue(null));

    private static object? EvaluateMember(MemberExpression member)
    {
        if (member.Member is not (FieldInfo or PropertyInfo))
        {
            return CompileAndEvaluate(member);
        }

        object? instance = null;
        if (member.Expression is not null)
        {
            instance = Evaluate(member.Expression);
        }

        var getter = GetterCache.GetOrAdd(member.Member, static m => m switch
        {
            FieldInfo field => CompileGetter(field.DeclaringType, field, isStatic: field.IsStatic),
            PropertyInfo property => CompileGetter(property.DeclaringType, property, isStatic: property.GetMethod?.IsStatic == true),
            _ => throw new InvalidOperationException($"Unsupported member type '{m.MemberType}'.")
        });

        return getter(instance);
    }

    private static Func<object?, object?> CompileGetter(Type? declaringType, MemberInfo member, bool isStatic)
    {
        var instanceParam = Expression.Parameter(typeof(object), "instance");
        Expression? target = null;
        if (!isStatic)
        {
            if (declaringType is null)
            {
                throw new InvalidOperationException($"Member '{member.Name}' has no declaring type.");
            }

            target = Expression.Convert(instanceParam, declaringType);
        }

        Expression access = member is FieldInfo field
            ? Expression.Field(target, field)
            : Expression.Property(target, (PropertyInfo)member);

        var boxed = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object?, object?>>(boxed, instanceParam).Compile();
    }

    private static object? EvaluateUnary(UnaryExpression unary)
    {
        var operand = Evaluate(unary.Operand);
        var shape = new UnaryShape(
            unary.NodeType, unary.Operand.Type, unary.Type, unary.Method, unary.IsLiftedToNull);
        var evaluate = UnaryCache.GetOrAdd(shape, static s => CompileUnary(s));
        return evaluate(operand);
    }

    private static Func<object?, object?> CompileUnary(UnaryShape shape)
    {
        var operand = Expression.Parameter(typeof(object), "operand");
        Expression body = Expression.MakeUnary(
            shape.Node,
            Expression.Convert(operand, shape.Operand),
            shape.Result,
            shape.Method);

        if (body.Type != typeof(object))
        {
            body = Expression.Convert(body, typeof(object));
        }

        return Expression.Lambda<Func<object?, object?>>(body, operand).Compile();
    }

    private static object? EvaluateBinary(BinaryExpression binary)
    {
        // Short-circuit and coalesce need their runtime semantics (and coalesce's
        // conversion lambda), so those stay on the compile path.
        if (binary.NodeType is ExpressionType.Coalesce or ExpressionType.AndAlso
            or ExpressionType.OrElse or ExpressionType.ArrayIndex)
        {
            return CompileAndEvaluate(binary);
        }

        var left = Evaluate(binary.Left);
        var right = Evaluate(binary.Right);
        var shape = new BinaryShape(
            binary.NodeType, binary.Left.Type, binary.Right.Type, binary.Type, binary.Method, binary.IsLiftedToNull);
        var evaluate = BinaryCache.GetOrAdd(shape, static s => CompileBinary(s));
        return evaluate(left, right);
    }

    private static Func<object?, object?, object?> CompileBinary(BinaryShape shape)
    {
        var left = Expression.Parameter(typeof(object), "left");
        var right = Expression.Parameter(typeof(object), "right");
        Expression body = Expression.MakeBinary(
            shape.Node,
            Expression.Convert(left, shape.Left),
            Expression.Convert(right, shape.Right),
            shape.IsLiftedToNull,
            shape.Method);

        if (body.Type != typeof(object))
        {
            body = Expression.Convert(body, typeof(object));
        }

        return Expression.Lambda<Func<object?, object?, object?>>(body, left, right).Compile();
    }

    private static object? CompileAndEvaluate(Expression expression)
    {
        // Keep the lambda's natural return type: adding Expression.Convert(object)
        // makes compilation itself more expensive than the DynamicInvoke it avoids.
        try
        {
            var lambda = Expression.Lambda(expression);
            return lambda.Compile().DynamicInvoke();
        }
        catch
        {
            var converted = Expression.Convert(expression, typeof(object));
            return Expression.Lambda<Func<object?>>(converted).Compile()();
        }
    }
}
