using System.Linq.Expressions;

namespace EF.Core.Extensions.Internal;

internal sealed class ParameterReplacer(ParameterExpression source, Expression replacement) : ExpressionVisitor
{
    public override Expression? Visit(Expression? node)
        => node == source ? replacement : base.Visit(node);

    public static Expression Replace(Expression body, ParameterExpression source, Expression replacement)
        => new ParameterReplacer(source, replacement).Visit(body)
           ?? throw new InvalidOperationException("Expression rewriting produced an empty body.");
}
