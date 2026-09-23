using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class ExpressionEvaluatorCache
{
    private static readonly ConcurrentDictionary<MemberInfo, Func<object?, object?>> GetterCache = new();

    private static readonly ConcurrentDictionary<BinaryOpKey, Func<object?, object?, object?>> BinaryOpCache = new();

    private static readonly ConcurrentDictionary<UnaryOpKey, Func<object?, object?>> UnaryOpCache = new();

    private static readonly ConcurrentDictionary<Type, object> DefaultComparerCache = new();

    private static readonly ConditionalWeakTable<Expression, Func<object?>> CompileCache = new();

    public static object? Evaluate(Expression expression)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                return constant.Value;
            case MemberExpression member:
                return EvaluateMember(member);
            case BinaryExpression binary:
                return EvaluateBinary(binary);
            case UnaryExpression unary:
                return EvaluateUnary(unary);
            case ConditionalExpression conditional:
            {
                var test = Evaluate(conditional.Test);
                return Evaluate(test is true ? conditional.IfTrue : conditional.IfFalse);
            }
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
            typeof(EqualityComparer<>).MakeGenericType(t).GetProperty("Default")?.GetValue(null)
            ?? throw new InvalidOperationException($"No EqualityComparer Default property for '{t}'."));

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

    private static object? EvaluateBinary(BinaryExpression binary)
    {
        switch (binary.NodeType)
        {
            case ExpressionType.AndAlso:
            {
                var left = Evaluate(binary.Left);
                if (left is false)
                {
                    return false;
                }

                return ApplyBinary(binary, left, Evaluate(binary.Right));
            }
            case ExpressionType.OrElse:
            {
                var left = Evaluate(binary.Left);
                if (left is true)
                {
                    return true;
                }

                return ApplyBinary(binary, left, Evaluate(binary.Right));
            }
            case ExpressionType.Coalesce:
            {
                var left = Evaluate(binary.Left);
                if (left is not null)
                {
                    return left;
                }

                if (binary.Conversion is not null)
                {
                    return CompileAndEvaluate(binary);
                }

                return Evaluate(binary.Right);
            }
            default:
                return ApplyBinary(binary, Evaluate(binary.Left), Evaluate(binary.Right));
        }
    }

    private static object? ApplyBinary(BinaryExpression binary, object? left, object? right)
    {
        var key = new BinaryOpKey(
            binary.NodeType,
            binary.Left.Type,
            binary.Right.Type,
            binary.Type,
            binary.Method,
            binary.IsLiftedToNull);

        var op = BinaryOpCache.GetOrAdd(key, _ => CompileBinaryOp(binary));
        return op(left, right);
    }

    private static Func<object?, object?, object?> CompileBinaryOp(BinaryExpression prototype)
    {
        var leftParam = Expression.Parameter(typeof(object), "left");
        var rightParam = Expression.Parameter(typeof(object), "right");
        var left = Expression.Convert(leftParam, prototype.Left.Type);
        var right = Expression.Convert(rightParam, prototype.Right.Type);
        var body = Expression.MakeBinary(prototype.NodeType, left, right, prototype.IsLiftedToNull, prototype.Method);
        var boxed = Expression.Convert(body, typeof(object));
        return Expression.Lambda<Func<object?, object?, object?>>(boxed, leftParam, rightParam).Compile();
    }

    private static object? EvaluateUnary(UnaryExpression unary)
    {
        switch (unary.NodeType)
        {
            case ExpressionType.Convert:
            case ExpressionType.ConvertChecked:
            {
                var operandType = unary.Operand.Type;
                if (unary.Method is null && (unary.Type == typeof(object) || operandType == unary.Type))
                {
                    return Evaluate(unary.Operand);
                }

                var key = new UnaryOpKey(unary.NodeType, operandType, unary.Type, unary.Method);
                var op = UnaryOpCache.GetOrAdd(key, _ => CompileUnaryOp(unary));
                return op(Evaluate(unary.Operand));
            }
            case ExpressionType.TypeAs:
            {
                var value = Evaluate(unary.Operand);
                if (value is null)
                {
                    return null;
                }

                return unary.Type.IsInstanceOfType(value) ? value : null;
            }
            case ExpressionType.Not:
            case ExpressionType.Negate:
            case ExpressionType.NegateChecked:
            case ExpressionType.UnaryPlus:
            case ExpressionType.IsTrue:
            case ExpressionType.IsFalse:
            {
                var key = new UnaryOpKey(unary.NodeType, unary.Operand.Type, unary.Type, unary.Method);
                var op = UnaryOpCache.GetOrAdd(key, _ => CompileUnaryOp(unary));
                return op(Evaluate(unary.Operand));
            }
            default:
                return CompileAndEvaluate(unary);
        }
    }

    private static Func<object?, object?> CompileUnaryOp(UnaryExpression prototype)
    {
        var valueParam = Expression.Parameter(typeof(object), "value");
        var operand = Expression.Convert(valueParam, prototype.Operand.Type);
        var body = Expression.MakeUnary(prototype.NodeType, operand, prototype.Type, prototype.Method);
        var boxed = Expression.Convert(body, typeof(object));
        return Expression.Lambda<Func<object?, object?>>(boxed, valueParam).Compile();
    }

    private static object? CompileAndEvaluate(Expression expression)
    {
        var compiled = CompileCache.GetValue(expression, static e =>
        {
            var body = e.Type == typeof(object) ? e : Expression.Convert(e, typeof(object));
            return Expression.Lambda<Func<object?>>(body).Compile();
        });

        return compiled();
    }

    private readonly record struct BinaryOpKey(
        ExpressionType NodeType,
        Type LeftType,
        Type RightType,
        Type ResultType,
        MethodInfo? Method,
        bool LiftToNull);

    private readonly record struct UnaryOpKey(
        ExpressionType NodeType,
        Type OperandType,
        Type ResultType,
        MethodInfo? Method);
}
