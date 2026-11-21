using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class ExpressionEvaluatorCache
{
    private static readonly ConcurrentDictionary<MemberInfo, Func<object?, object?>> GetterCache = new();

    private static readonly ConcurrentDictionary<Type, object?> DefaultComparerCache = new();

    private static readonly ConditionalWeakTable<Expression, Func<object?>> CompileCache = new();

    public static object? Evaluate(Expression expression)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                return constant.Value;
            case MemberExpression member:
                return EvaluateMember(member);
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
            // Operators, conversions and conditionals are rare in captured subtrees.
            // Compile them once per expression instance (weakly held) instead of
            // duplicating C# operator semantics here.
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

    private static object? CompileAndEvaluate(Expression expression)
    {
        var compiled = CompileCache.GetValue(expression, static e =>
        {
            var body = e.Type == typeof(object) ? e : Expression.Convert(e, typeof(object));
            return Expression.Lambda<Func<object?>>(body).Compile();
        });

        return compiled();
    }
}
