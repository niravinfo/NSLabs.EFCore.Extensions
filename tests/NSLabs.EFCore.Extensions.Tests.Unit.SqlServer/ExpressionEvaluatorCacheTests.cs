using System.Linq.Expressions;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

public class ExpressionEvaluatorCacheTests
{
    [Fact]
    public void Evaluate_coalesce_with_conversion_matches_compiled_runtime()
    {
        var left = Expression.Constant((int?)3, typeof(int?));
        Expression<Func<int, long>> conversion = i => i + 1L;
        var coalesce = Expression.Coalesce(left, Expression.Constant(4L), conversion);

        var compiled = Expression.Lambda<Func<long>>(coalesce).Compile().Invoke();
        var evaluated = ExpressionEvaluatorCache.Evaluate(coalesce);

        Assert.Equal(compiled, evaluated);
        Assert.IsType<long>(evaluated);
    }

    [Fact]
    public void Evaluate_uses_current_value_for_each_instance()
    {
        var first = new Holder { Value = 1 };
        var second = new Holder { Value = 9 };
        var property = typeof(Holder).GetProperty(nameof(Holder.Value))!;

        var firstValue = ExpressionEvaluatorCache.Evaluate(Expression.Property(Expression.Constant(first), property));
        var secondValue = ExpressionEvaluatorCache.Evaluate(Expression.Property(Expression.Constant(second), property));

        Assert.Equal(1, firstValue);
        Assert.Equal(9, secondValue);
    }

    [Fact]
    public void Evaluate_reads_nested_member_chain()
    {
        var parent = new Holder { Child = new Holder { Value = 7 } };
        var chain = Expression.Property(
            Expression.Property(Expression.Constant(parent), nameof(Holder.Child)),
            nameof(Holder.Value));

        Assert.Equal(7, ExpressionEvaluatorCache.Evaluate(chain));
    }

    [Fact]
    public void Evaluate_handles_new_array_and_list_initializer()
    {
        var array = ExpressionEvaluatorCache.Evaluate(
            Expression.NewArrayInit(typeof(int), Expression.Constant(1), Expression.Constant(2)));

        Assert.Equal([1, 2], Assert.IsType<int[]>(array));

        var list = ExpressionEvaluatorCache.Evaluate(
            Expression.ListInit(
                Expression.New(typeof(List<int>)),
                Expression.ElementInit(typeof(List<int>).GetMethod(nameof(List<int>.Add))!, Expression.Constant(3))));

        Assert.Equal([3], Assert.IsType<List<int>>(list));
    }

    [Fact]
    public void Evaluate_falls_back_to_compile_for_unsupported_node_shapes()
    {
        var call = Expression.Call(
            Expression.Constant("abc"),
            typeof(string).GetMethod(nameof(string.ToUpperInvariant), Type.EmptyTypes)!);

        Assert.Equal("ABC", ExpressionEvaluatorCache.Evaluate(call));
    }

    [Fact]
    public void Evaluate_binary_reuses_shape_with_new_values()
    {
        var first = Expression.Add(Expression.Constant(1), Expression.Constant(2));
        var second = Expression.Add(Expression.Constant(10), Expression.Constant(20));

        Assert.Equal(3, ExpressionEvaluatorCache.Evaluate(first));
        Assert.Equal(30, ExpressionEvaluatorCache.Evaluate(second));
    }

    [Fact]
    public void Evaluate_binary_matches_compiled_runtime_for_multiple_types()
    {
        Expression[] cases =
        [
            Expression.Add(Expression.Constant(1), Expression.Constant(2)),
            Expression.Multiply(Expression.Constant(1.5m), Expression.Constant(2m)),
            Expression.Add(
                Expression.Constant("a"),
                Expression.Constant("b"),
                typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])),
            Expression.Subtract(Expression.Constant(7L), Expression.Constant(2L)),
        ];

        foreach (var expression in cases)
        {
            var expected = Expression.Lambda<Func<object>>(
                Expression.Convert(expression, typeof(object))).Compile().Invoke();
            Assert.Equal(expected, ExpressionEvaluatorCache.Evaluate(expression));
        }
    }

    [Fact]
    public void Evaluate_unary_convert_matches_compiled_runtime()
    {
        var expression = Expression.Convert(Expression.Constant(5), typeof(long));

        Assert.Equal(5L, ExpressionEvaluatorCache.Evaluate(expression));
    }

    [Fact]
    public void Evaluate_lifted_nullable_arithmetic_matches_compiled_runtime()
    {
        var expression = Expression.Add(
            Expression.Constant((int?)2, typeof(int?)),
            Expression.Constant((int?)3, typeof(int?)));

        var expected = Expression.Lambda<Func<object>>(
            Expression.Convert(expression, typeof(object))).Compile().Invoke();
        Assert.Equal(expected, ExpressionEvaluatorCache.Evaluate(expression));
    }

    [Fact]
    public void Evaluate_user_defined_operator_uses_operator_method()
    {
        var expression = Expression.Add(Expression.Constant(new Money(2)), Expression.Constant(new Money(3)));

        Assert.Equal(new Money(5), ExpressionEvaluatorCache.Evaluate(expression));
    }

    [Fact]
    public void Evaluate_does_not_evaluate_short_circuit_right_operand()
    {
        var thrower = Expression.Call(typeof(ExpressionEvaluatorCacheTests)
            .GetMethod(nameof(Throw), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!);

        Assert.False((bool)ExpressionEvaluatorCache.Evaluate(
            Expression.AndAlso(Expression.Constant(false), thrower))!);
    }

    private static bool Throw() => throw new InvalidOperationException("must not be evaluated");

    private readonly record struct Money(int Amount)
    {
        public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount);
    }

    private sealed class Holder
    {
        public int Value { get; set; }

        public Holder? Child { get; set; }
    }
}
