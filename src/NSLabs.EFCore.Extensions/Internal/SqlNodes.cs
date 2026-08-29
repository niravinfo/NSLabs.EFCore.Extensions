using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal abstract class SqlNode;

internal sealed class SqlColumnNode(IProperty property) : SqlNode
{
    public IProperty Property { get; } = property;

    public bool ConvertedInTree { get; init; }
}

internal sealed class SqlBooleanNode(IProperty property) : SqlNode
{
    public IProperty Property { get; } = property;
}

internal sealed class SqlParameterNode(object? value) : SqlNode
{
    public object? Value { get; } = value;
}

internal enum SqlBinaryOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    And,
    Or,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo
}

internal enum SqlUnaryOperator
{
    Negate
}

internal sealed class SqlBinaryNode(SqlBinaryOperator op, SqlNode left, SqlNode right) : SqlNode
{
    public SqlBinaryOperator Operator { get; } = op;

    public SqlNode Left { get; } = left;

    public SqlNode Right { get; } = right;
}

internal sealed class SqlNotNode(SqlNode inner) : SqlNode
{
    public SqlNode Inner { get; } = inner;
}

internal sealed class SqlNullCheckNode(IProperty property, bool isNotNull) : SqlNode
{
    public IProperty Property { get; } = property;

    public bool IsNotNull { get; } = isNotNull;
}

internal sealed class SqlUnaryNode(SqlUnaryOperator op, SqlNode inner) : SqlNode
{
    public SqlUnaryOperator Operator { get; } = op;

    public SqlNode Inner { get; } = inner;
}

internal sealed class SqlConditionalNode(SqlNode test, SqlNode ifTrue, SqlNode ifFalse) : SqlNode
{
    public SqlNode Test { get; } = test;

    public SqlNode IfTrue { get; } = ifTrue;

    public SqlNode IfFalse { get; } = ifFalse;
}

internal sealed class SqlCoalesceNode(SqlNode left, SqlNode right) : SqlNode
{
    public SqlNode Left { get; } = left;

    public SqlNode Right { get; } = right;
}

internal sealed class SqlMethodCallNode(string method, IReadOnlyList<SqlNode> args) : SqlNode
{
    public string Method { get; } = method;

    public IReadOnlyList<SqlNode> Args { get; } = args;
}
