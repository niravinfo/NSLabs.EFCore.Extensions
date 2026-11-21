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

    private sealed class Holder
    {
        public int Value { get; set; }

        public Holder? Child { get; set; }
    }
}
