using System.Text;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class SqlServerSqlGenerator
{
    private const string TargetAlias = "t";

    private const string SourceAlias = "s";

    public static IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParametersPerCommand, 1);

        var chunks = new List<SqlChunkPlan>();
        var pending = new List<PendingUnit>();
        var pendingParamCount = 0;

        foreach (var operation in operations)
        {
            if (operation.Kind == BulkOperationKind.Upsert)
            {
                ExpandUpsert(operation, pending, chunks, maxParametersPerCommand, ref pendingParamCount);
                continue;
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

            pending.Add(new PendingUnit(operation, 0, 0));
            pendingParamCount += cost;
        }

        if (pending.Count > 0)
        {
            chunks.Add(BuildChunk(pending));
        }

        return chunks;
    }

    /// <summary>
    /// Splits an upsert's rows into one or more units that each fit the parameter budget.
    /// Units are appended to <paramref name="pending"/>; when a later unit cannot share the
    /// current chunk with earlier ones, the pending buffer is flushed first.
    /// </summary>
    private static void ExpandUpsert(
        BoundOperation operation,
        List<PendingUnit> pending,
        List<SqlChunkPlan> chunks,
        int maxParametersPerCommand,
        ref int pendingParamCount)
    {
        if (operation.UpsertSpec is not { } spec)
        {
            throw new InvalidOperationException($"Upsert operation #{operation.GlobalIndex} was not bound to an upsert spec.");
        }

        if (spec.Rows.Count == 0)
        {
            pending.Add(new PendingUnit(operation, 0, 0));
            return;
        }

        var fixedCost = CountParameterNodes(spec.Guard) + operation.Assignments.Count;
        var perRowCost = spec.InsertColumns.Count;

        if (fixedCost + perRowCost > maxParametersPerCommand)
        {
            throw new InvalidOperationException(
                $"Operation #{operation.GlobalIndex} requires {fixedCost + perRowCost} parameters for a single upsert row which exceeds MaxParametersPerCommand={maxParametersPerCommand}. Increase the limit or split the operation.");
        }

        var startRow = 0;
        while (startRow < spec.Rows.Count)
        {
            var capacity = (maxParametersPerCommand - pendingParamCount - fixedCost) / perRowCost;

            if (capacity <= 0)
            {
                FlushPending(pending, chunks, ref pendingParamCount);
                continue;
            }

            var rowCount = Math.Min(spec.Rows.Count - startRow, capacity);
            pending.Add(new PendingUnit(operation, startRow, rowCount));
            pendingParamCount += fixedCost + rowCount * perRowCost;
            startRow += rowCount;

            // The remaining rows cannot join this chunk (another row never fits after a full
            // fill), so flush now to keep chunk boundaries clean for subsequent operations.
            if (startRow < spec.Rows.Count)
            {
                FlushPending(pending, chunks, ref pendingParamCount);
            }
        }
    }

    private static void FlushPending(List<PendingUnit> pending, List<SqlChunkPlan> chunks, ref int pendingParamCount)
    {
        if (pending.Count == 0)
        {
            return;
        }

        chunks.Add(BuildChunk(pending));
        pending.Clear();
        pendingParamCount = 0;
    }

    private static SqlChunkPlan BuildChunk(IReadOnlyList<PendingUnit> units)
    {
        var emitter = new ParameterEmitter();
        var sql = new StringBuilder();

        foreach (var index in OperationIndicesOf(units))
        {
            sql.Append("DECLARE @rc").Append(index).AppendLine(" int;");
        }

        foreach (var unit in units)
        {
            if (unit.Operation.Kind == BulkOperationKind.Upsert && unit.RowCount == 0)
            {
                sql.Append("SET @rc").Append(unit.Operation.GlobalIndex).AppendLine(" = 0;");
                continue;
            }

            if (unit.Operation.Kind == BulkOperationKind.Upsert)
            {
                EmitMerge(emitter, sql, unit.Operation, unit.StartRow, unit.RowCount);
            }
            else
            {
                EmitStatement(emitter, sql, unit.Operation);
            }

            sql.Append("SET @rc").Append(unit.Operation.GlobalIndex).AppendLine(" = @@ROWCOUNT;");
        }

        sql.Append("SELECT ")
            .Append(string.Join(", ", OperationIndicesOf(units).Select(index => $"@rc{index} AS Op{index}")))
            .Append(';');

        return new SqlChunkPlan
        {
            CommandText = sql.ToString(),
            Parameters = emitter.Parameters,
            OperationIndices = OperationIndicesOf(units).ToArray()
        };
    }

    private static IEnumerable<int> OperationIndicesOf(IReadOnlyList<PendingUnit> units)
        => units.Select(unit => unit.Operation.GlobalIndex).Distinct();

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
                        .Append(emitter.EmitValue(assignment.Value));
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

    private static void EmitMerge(ParameterEmitter emitter, StringBuilder sql, BoundOperation operation, int startRow, int rowCount)
    {
        var spec = operation.UpsertSpec!;
        var entityType = operation.EntityType;
        var insertColumnNames = spec.InsertColumns.Select(column => ModelBinder.GetColumnName(column, entityType)).ToArray();

        sql.Append("MERGE INTO ")
            .Append(Quote(ModelBinder.GetTableName(entityType)))
            .Append(" WITH (HOLDLOCK) AS [")
            .Append(TargetAlias)
            .Append(']');

        sql.Append(" USING (VALUES ");
        for (var r = 0; r < rowCount; r++)
        {
            if (r > 0)
            {
                sql.Append(", ");
            }

            sql.Append('(');
            var row = spec.Rows[startRow + r];
            for (var c = 0; c < row.InsertValues.Count; c++)
            {
                if (c > 0)
                {
                    sql.Append(", ");
                }

                sql.Append(emitter.EmitValue(row.InsertValues[c].Value));
            }

            sql.Append(')');
        }

        sql.Append(") AS [")
            .Append(SourceAlias)
            .Append("] (")
            .Append(string.Join(", ", insertColumnNames.Select(Quote)))
            .Append(')');

        sql.Append(" ON ")
            .Append(string.Join(" AND ", spec.ConflictProperties.Select(property =>
            {
                var column = Quote(ModelBinder.GetColumnName(property, entityType));
                return $"[{TargetAlias}].{column} = [{SourceAlias}].{column}";
            })));

        var hasMatchedUpdatePayload = operation.Assignments.Count > 0 || spec.UpdateColumns.Count > 0;
        if (hasMatchedUpdatePayload)
        {
            sql.Append(" WHEN MATCHED");

            if (spec.Guard is { } guard)
            {
                sql.Append(" AND ").Append(emitter.Emit(guard, entityType, TargetAlias));
            }

            sql.Append(" THEN UPDATE SET ");

            if (operation.Assignments.Count > 0)
            {
                for (var i = 0; i < operation.Assignments.Count; i++)
                {
                    if (i > 0)
                    {
                        sql.Append(", ");
                    }

                    var assignment = operation.Assignments[i];
                    sql.Append(Quote(ModelBinder.GetColumnName(assignment.Property, entityType)))
                        .Append(" = ")
                        .Append(emitter.EmitValue(assignment.Value));
                }
            }
            else
            {
                for (var i = 0; i < spec.UpdateColumns.Count; i++)
                {
                    if (i > 0)
                    {
                        sql.Append(", ");
                    }

                    var column = Quote(ModelBinder.GetColumnName(spec.UpdateColumns[i], entityType));
                    sql.Append(column).Append(" = [").Append(SourceAlias).Append("].").Append(column);
                }
            }
        }

        sql.Append(" WHEN NOT MATCHED THEN INSERT (")
            .Append(string.Join(", ", insertColumnNames.Select(Quote)))
            .Append(") VALUES (")
            .Append(string.Join(", ", insertColumnNames.Select(column => $"[{SourceAlias}].{Quote(column)}")))
            .Append(");")
            .AppendLine();
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

    private static int CountParameterNodes(SqlNode? node) => node switch
    {
        null => 0,
        SqlParameterNode => 1,
        SqlBinaryNode binary => CountParameterNodes(binary.Left) + CountParameterNodes(binary.Right),
        SqlNotNode not => CountParameterNodes(not.Inner),
        _ => 0
    };

    internal static string Quote(string identifier)
        => "[" + identifier.Replace("]", "]]") + "]";

    private readonly record struct PendingUnit(BoundOperation Operation, int StartRow, int RowCount);

    private sealed class ParameterEmitter
    {
        private readonly List<SqlParam> _parameters = [];

        public IReadOnlyList<SqlParam> Parameters => _parameters;

        private int Counter { get; set; }

        public string Emit(SqlNode node, IEntityType entityType, string? alias = null) => node switch
        {
            SqlColumnNode column => WithAlias(alias) + Quote(ModelBinder.GetColumnName(column.Property, entityType)),
            SqlBooleanNode boolean => $"{WithAlias(alias)}{Quote(ModelBinder.GetColumnName(boolean.Property, entityType))} = 1",
            SqlParameterNode parameter => EmitValue(parameter.Value),
            SqlNullCheckNode nullCheck =>
                $"{WithAlias(alias)}{Quote(ModelBinder.GetColumnName(nullCheck.Property, entityType))} {(nullCheck.IsNotNull ? "IS NOT NULL" : "IS NULL")}",
            SqlNotNode not => $"NOT ({Emit(not.Inner, entityType, alias)})",
            SqlBinaryNode { Operator: SqlBinaryOperator.And or SqlBinaryOperator.Or } logical =>
                $"({Emit(logical.Left, entityType, alias)} {(logical.Operator == SqlBinaryOperator.And ? "AND" : "OR")} {Emit(logical.Right, entityType, alias)})",
            SqlBinaryNode comparison =>
                $"{Emit(comparison.Left, entityType, alias)} {RenderComparison(comparison.Operator)} {Emit(comparison.Right, entityType, alias)}",
            _ => throw new NotSupportedException($"Cannot emit node '{node.GetType().Name}'.")
        };

        public string EmitValue(object? value)
        {
            var name = $"@p{Counter++}";
            _parameters.Add(new SqlParam(name, value));
            return name;
        }

        private static string WithAlias(string? alias) => alias is null ? "" : $"[{alias}].";

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
