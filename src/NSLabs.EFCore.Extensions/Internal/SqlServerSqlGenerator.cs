using System.Text;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal sealed record SqlParam(string Name, object? Value);

internal sealed class SqlChunkPlan
{
    public required string CommandText { get; init; }

    public required IReadOnlyList<SqlParam> Parameters { get; init; }

    public required IReadOnlyList<int> OperationIndices { get; init; }
}

internal static class SqlServerSqlGenerator
{
    public static IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParametersPerCommand, 1);

        var chunks = new List<SqlChunkPlan>();
        var pending = new List<BoundOperation>();
        var pendingParamCount = 0;

        foreach (var operation in operations)
        {
            if (operation.Kind == BulkOperationKind.Upsert)
            {
                throw new NotImplementedException(
                    $"Upsert operation #{operation.GlobalIndex}: SQL Server MERGE generation ships in milestone M2.");
            }

            var cost = CountParameters(operation);

            if (cost > maxParametersPerCommand)
            {
                throw new InvalidOperationException(
                    $"Operation #{operation.GlobalIndex} requires {cost} parameters which exceeds MaxParametersPerCommand={maxParametersPerCommand}. Increase the limit or split the operation.");
            }

            if (pendingParamCount + cost > maxParametersPerCommand && pending.Count > 0)
            {
                chunks.Add(BuildChunk(pending));
                pending = [];
                pendingParamCount = 0;
            }

            pending.Add(operation);
            pendingParamCount += cost;
        }

        if (pending.Count > 0)
        {
            chunks.Add(BuildChunk(pending));
        }

        return chunks;
    }

    private static SqlChunkPlan BuildChunk(IReadOnlyList<BoundOperation> operations)
    {
        var emitter = new ParameterEmitter();
        var sql = new StringBuilder();

        for (var k = 0; k < operations.Count; k++)
        {
            sql.Append("DECLARE @rc").Append(operations[k].GlobalIndex).AppendLine(" int;");
        }

        for (var k = 0; k < operations.Count; k++)
        {
            EmitStatement(emitter, sql, operations[k]);
            sql.Append("SET @rc").Append(operations[k].GlobalIndex).AppendLine(" = @@ROWCOUNT;");
        }

        sql.Append("SELECT ")
            .Append(string.Join(", ", operations.Select(op => $"@rc{op.GlobalIndex} AS Op{op.GlobalIndex}")))
            .Append(';');

        return new SqlChunkPlan
        {
            CommandText = sql.ToString(),
            Parameters = emitter.Parameters,
            OperationIndices = operations.Select(op => op.GlobalIndex).ToArray()
        };
    }

    private static void EmitStatement(ParameterEmitter emitter, StringBuilder sql, BoundOperation operation)
    {
        var table = Quote(ModelBinder.GetTableName(operation.EntityType));

        switch (operation.Kind)
        {
            case BulkOperationKind.Update:
                if (operation.Assignments.Count == 0)
                {
                    throw new InvalidOperationException($"Update operation #{operation.GlobalIndex} has no assignments.");
                }

                sql.Append("UPDATE ").Append(table).Append(" SET ");

                for (var i = 0; i < operation.Assignments.Count; i++)
                {
                    if (i > 0)
                    {
                        sql.Append(", ");
                    }

                    var assignment = operation.Assignments[i];
                    sql.Append(Quote(ModelBinder.GetColumnName(assignment.Property, operation.EntityType)))
                        .Append(" = ")
                        .Append(emitter.Emit(new SqlParameterNode(assignment.Value), operation.EntityType));
                }

                break;

            case BulkOperationKind.Delete:
                sql.Append("DELETE FROM ").Append(table);
                break;

            default:
                throw new NotSupportedException($"Operation kind '{operation.Kind}' cannot be emitted as a simple statement.");
        }

        sql.Append(" WHERE ").Append(EmitPredicate(emitter, operation)).Append(';').AppendLine();
    }

    private static string EmitPredicate(ParameterEmitter emitter, BoundOperation operation)
    {
        if (operation.PredicateParts.Count == 0)
        {
            throw new InvalidOperationException(
                $"Operation #{operation.GlobalIndex} on '{operation.EntityType.DisplayName()}' has no predicate; refusing to emit unbounded DML.");
        }

        return string.Join(" AND ", operation.PredicateParts.Select(part => emitter.Emit(part, operation.EntityType)));
    }

    private static int CountParameters(BoundOperation operation)
    {
        var count = operation.Assignments.Count;

        foreach (var part in operation.PredicateParts)
        {
            count += CountParameterNodes(part);
        }

        return count;
    }

    private static int CountParameterNodes(SqlNode node) => node switch
    {
        SqlParameterNode => 1,
        SqlBinaryNode binary => CountParameterNodes(binary.Left) + CountParameterNodes(binary.Right),
        SqlNotNode not => CountParameterNodes(not.Inner),
        _ => 0
    };

    internal static string Quote(string identifier)
        => "[" + identifier.Replace("]", "]]") + "]";

    private sealed class ParameterEmitter
    {
        private readonly List<SqlParam> _parameters = [];

        public IReadOnlyList<SqlParam> Parameters => _parameters;

        private int Counter { get; set; }

        public string Emit(SqlNode node, IEntityType entityType) => node switch
        {
            SqlColumnNode column => Quote(ModelBinder.GetColumnName(column.Property, entityType)),
            SqlBooleanNode boolean => $"{Quote(ModelBinder.GetColumnName(boolean.Property, entityType))} = 1",
            SqlParameterNode parameter => EmitParameter(parameter.Value),
            SqlNullCheckNode nullCheck =>
                $"{Quote(ModelBinder.GetColumnName(nullCheck.Property, entityType))} {(nullCheck.IsNotNull ? "IS NOT NULL" : "IS NULL")}",
            SqlNotNode not => $"NOT ({Emit(not.Inner, entityType)})",
            SqlBinaryNode { Operator: SqlBinaryOperator.And or SqlBinaryOperator.Or } logical =>
                $"({Emit(logical.Left, entityType)} {(logical.Operator == SqlBinaryOperator.And ? "AND" : "OR")} {Emit(logical.Right, entityType)})",
            SqlBinaryNode comparison =>
                $"{Emit(comparison.Left, entityType)} {RenderComparison(comparison.Operator)} {Emit(comparison.Right, entityType)}",
            _ => throw new NotSupportedException($"Cannot emit node '{node.GetType().Name}'.")
        };

        private string EmitParameter(object? value)
        {
            var name = $"@p{Counter++}";
            _parameters.Add(new SqlParam(name, value));
            return name;
        }

        private static string RenderComparison(SqlBinaryOperator op) => op switch
        {
            SqlBinaryOperator.Equal => "=",
            SqlBinaryOperator.NotEqual => "<>",
            SqlBinaryOperator.LessThan => "<",
            SqlBinaryOperator.LessThanOrEqual => "<=",
            SqlBinaryOperator.GreaterThan => ">",
            SqlBinaryOperator.GreaterThanOrEqual => ">=",
            _ => throw new NotSupportedException($"Operator '{op}' is not a comparison.")
        };
    }
}
