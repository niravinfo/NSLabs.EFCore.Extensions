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

        // SAFETY S8: presizing never changes chunk boundaries; only reduces reallocations
        var chunks = new List<SqlChunkPlan>(Math.Min(operations.Count, 16));
        var pending = new List<PendingUnit>(Math.Min(operations.Count, 16));
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

        // EF Core pattern: manual loop vs LINQ Sum — avoids enumerator alloc in hot ExpandUpsert
        var fixedCost = CountParameterNodes(spec.Guard);
        for (var i = 0; i < operation.Assignments.Count; i++)
        {
            var assignment = operation.Assignments[i];
            fixedCost += assignment.ValueExpression is not null ? CountParameterNodes(assignment.ValueExpression) : 1;
        }

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
        // SAFETY S2,S11: distinct indices computed once, order preserved (insertion order); SQL is identical
        var distinctIndices = GetDistinctIndices(units);
        var estimatedParamCount = 0;
        foreach (var u in units)
        {
            // Rough estimate for presizing; exact count not required for correctness
            estimatedParamCount += u.Operation.Kind == BulkOperationKind.Upsert ? u.RowCount * 3 : 3;
        }

        var emitter = new ParameterEmitter(Math.Max(estimatedParamCount, 4));
        // EF Core pattern: StringBuilderCache (ThreadStatic pooling, max 1024) — reduces Gen0 per BuildChunk in hot loops
        var sql = StringBuilderCache.Acquire(256 + (units.Count * 180) + (distinctIndices.Count * 32));

        foreach (var index in distinctIndices)
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

        sql.Append("SELECT ");
        for (var i = 0; i < distinctIndices.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            var idx = distinctIndices[i];
            sql.Append("@rc").Append(idx).Append(" AS Op").Append(idx);
        }
        sql.Append(';');

        return new SqlChunkPlan
        {
            CommandText = StringBuilderCache.GetStringAndRelease(sql),
            Parameters = emitter.Parameters,
            OperationIndices = distinctIndices.ToArray()
        };
    }

    private static List<int> GetDistinctIndices(IReadOnlyList<PendingUnit> units)
    {
        var seen = new HashSet<int>();
        var list = new List<int>(units.Count);
        foreach (var unit in units)
        {
            if (seen.Add(unit.Operation.GlobalIndex))
            {
                list.Add(unit.Operation.GlobalIndex);
            }
        }
        return list;
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
                        .Append(assignment.ValueExpression is not null
                            ? emitter.Emit(assignment.ValueExpression, operation.EntityType)
                            : emitter.EmitValue(assignment.Value));
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

        // EF Core pattern: manual loop vs Select+ToArray+string.Join — avoids 2 allocs per MERGE
        sql.Append(") AS [")
            .Append(SourceAlias)
            .Append("] (");
        for (var i = 0; i < spec.InsertColumns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(Quote(ModelBinder.GetColumnName(spec.InsertColumns[i], entityType)));
        }
        sql.Append(')');

        sql.Append(" ON ");
        for (var i = 0; i < spec.ConflictProperties.Count; i++)
        {
            if (i > 0) sql.Append(" AND ");
            var column = Quote(ModelBinder.GetColumnName(spec.ConflictProperties[i], entityType));
            sql.Append('[').Append(TargetAlias).Append("].").Append(column)
               .Append(" = [").Append(SourceAlias).Append("].").Append(column);
        }

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
                        .Append(assignment.ValueExpression is not null
                            ? emitter.Emit(assignment.ValueExpression, entityType, TargetAlias)
                            : emitter.EmitValue(assignment.Value));
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

        sql.Append(" WHEN NOT MATCHED THEN INSERT (");
        for (var i = 0; i < spec.InsertColumns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(Quote(ModelBinder.GetColumnName(spec.InsertColumns[i], entityType)));
        }
        sql.Append(") VALUES (");
        for (var i = 0; i < spec.InsertColumns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            var col = Quote(ModelBinder.GetColumnName(spec.InsertColumns[i], entityType));
            sql.Append('[').Append(SourceAlias).Append("].").Append(col);
        }
        sql.Append(");")
            .AppendLine();
    }

    private static string EmitPredicate(ParameterEmitter emitter, BoundOperation operation)
    {
        if (operation.PredicateParts.Count == 0)
        {
            throw new InvalidOperationException(
                $"Operation #{operation.GlobalIndex} on '{operation.EntityType.DisplayName()}' has no predicate; refusing to emit unbounded DML.");
        }

        // EF Core pattern: manual StringBuilder vs string.Join + Select — avoids LINQ alloc per UPDATE/DELETE
        if (operation.PredicateParts.Count == 1)
        {
            return emitter.Emit(operation.PredicateParts[0], operation.EntityType);
        }

        var sb = new StringBuilder(64);
        for (var i = 0; i < operation.PredicateParts.Count; i++)
        {
            if (i > 0) sb.Append(" AND ");
            sb.Append(emitter.Emit(operation.PredicateParts[i], operation.EntityType));
        }

        return sb.ToString();
    }

    private static int CountParameters(BoundOperation operation)
    {
        // EF Core pattern: manual loop vs LINQ Sum
        var count = 0;
        for (var i = 0; i < operation.Assignments.Count; i++)
        {
            var assignment = operation.Assignments[i];
            count += assignment.ValueExpression is not null ? CountParameterNodes(assignment.ValueExpression) : 1;
        }

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
        SqlUnaryNode unary => CountParameterNodes(unary.Inner),
        SqlConditionalNode cond => CountParameterNodes(cond.Test) + CountParameterNodes(cond.IfTrue) + CountParameterNodes(cond.IfFalse),
        SqlCoalesceNode co => CountParameterNodes(co.Left) + CountParameterNodes(co.Right),
        SqlMethodCallNode method => CountMethodArgs(method),
        SqlColumnNode => 0,
        SqlBooleanNode => 0,
        SqlNullCheckNode => 0,
        SqlLikeNode => 1,
        SqlInNode inNode => inNode.Values.Count,
        SqlIsEmptyNode => 0,
        _ => 0
    };

    private static int CountMethodArgs(SqlMethodCallNode method)
    {
        // EF Core pattern: manual loop vs LINQ Sum
        var sum = 0;
        for (var i = 0; i < method.Args.Count; i++)
        {
            sum += CountParameterNodes(method.Args[i]);
        }

        return sum;
    }

    internal static string Quote(string identifier)
    {
        // SAFETY S6: fast-path avoids Replace allocation when no escaping needed; identical result
        if (identifier.IndexOf(']') < 0)
        {
            return "[" + identifier + "]";
        }

        return "[" + identifier.Replace("]", "]]") + "]";
    }

    private readonly record struct PendingUnit(BoundOperation Operation, int StartRow, int RowCount);

    private sealed class ParameterEmitter
    {
        private readonly List<SqlParam> _parameters;

        public IReadOnlyList<SqlParam> Parameters => _parameters;

        private int Counter { get; set; }

        public ParameterEmitter(int capacity = 8)
        {
            _parameters = new List<SqlParam>(capacity);
        }

        public string Emit(SqlNode node, IEntityType entityType, string? alias = null) => node switch
        {
            SqlColumnNode column => WithAlias(alias) + Quote(ModelBinder.GetColumnName(column.Property, entityType)),
            SqlBooleanNode boolean => $"{WithAlias(alias)}{Quote(ModelBinder.GetColumnName(boolean.Property, entityType))} = 1",
            SqlParameterNode parameter => EmitValue(parameter.Value),
            SqlNullCheckNode nullCheck =>
                $"{WithAlias(alias)}{Quote(ModelBinder.GetColumnName(nullCheck.Property, entityType))} {(nullCheck.IsNotNull ? "IS NOT NULL" : "IS NULL")}",
            SqlNotNode not => $"NOT ({Emit(not.Inner, entityType, alias)})",
            SqlUnaryNode unary => EmitUnary(unary, entityType, alias),
            SqlConditionalNode cond => $"CASE WHEN {Emit(cond.Test, entityType, alias)} THEN {Emit(cond.IfTrue, entityType, alias)} ELSE {Emit(cond.IfFalse, entityType, alias)} END",
            SqlCoalesceNode co => $"COALESCE({Emit(co.Left, entityType, alias)}, {Emit(co.Right, entityType, alias)})",
            SqlMethodCallNode method => EmitMethod(method, entityType, alias),
            SqlLikeNode like => EmitLike(like, entityType, alias),
            SqlInNode inNode => EmitIn(inNode, entityType, alias),
            SqlIsEmptyNode empty => EmitIsEmpty(empty, entityType, alias),
            SqlBinaryNode { Operator: SqlBinaryOperator.And or SqlBinaryOperator.Or } logical =>
                $"({Emit(logical.Left, entityType, alias)} {(logical.Operator == SqlBinaryOperator.And ? "AND" : "OR")} {Emit(logical.Right, entityType, alias)})",
            SqlBinaryNode arithmetic when IsArithmetic(arithmetic.Operator) =>
                $"({Emit(arithmetic.Left, entityType, alias)} {RenderArithmetic(arithmetic.Operator)} {Emit(arithmetic.Right, entityType, alias)})",
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

        private static bool IsArithmetic(SqlBinaryOperator op) => op is SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo;

        private static string RenderArithmetic(SqlBinaryOperator op) => op switch
        {
            SqlBinaryOperator.Add => "+",
            SqlBinaryOperator.Subtract => "-",
            SqlBinaryOperator.Multiply => "*",
            SqlBinaryOperator.Divide => "/",
            SqlBinaryOperator.Modulo => "%",
            _ => throw new NotSupportedException($"Operator '{op}' is not an arithmetic operator.")
        };

        private string EmitUnary(SqlUnaryNode unary, IEntityType entityType, string? alias)
            => unary.Operator switch
            {
                SqlUnaryOperator.Negate => $"-{Emit(unary.Inner, entityType, alias)}",
                _ => throw new NotSupportedException($"Unary operator '{unary.Operator}' is not supported.")
            };

        private string EmitMethod(SqlMethodCallNode method, IEntityType entityType, string? alias)
        {
            // EF Core pattern: switch with manual string building — avoids LINQ in CONCAT
            switch (method.Method)
            {
                case "UPPER": return $"UPPER({Emit(method.Args[0], entityType, alias)})";
                case "LOWER": return $"LOWER({Emit(method.Args[0], entityType, alias)})";
                case "TRIM": return $"LTRIM(RTRIM({Emit(method.Args[0], entityType, alias)}))";
                case "LTRIM": return $"LTRIM({Emit(method.Args[0], entityType, alias)})";
                case "RTRIM": return $"RTRIM({Emit(method.Args[0], entityType, alias)})";
                case "LEN": return $"LEN({Emit(method.Args[0], entityType, alias)})";
                case "SUBSTRING": return $"SUBSTRING({Emit(method.Args[0], entityType, alias)}, {Emit(method.Args[1], entityType, alias)}, {Emit(method.Args[2], entityType, alias)})";
                case "REPLACE": return $"REPLACE({Emit(method.Args[0], entityType, alias)}, {Emit(method.Args[1], entityType, alias)}, {Emit(method.Args[2], entityType, alias)})";
                case "CONCAT":
                {
                    // Manual loop vs string.Join + Select — preserves Emit side-effects order
                    var sb = new StringBuilder("CONCAT(");
                    for (var i = 0; i < method.Args.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(Emit(method.Args[i], entityType, alias));
                    }
                    sb.Append(')');
                    return sb.ToString();
                }
                case "ABS": return $"ABS({Emit(method.Args[0], entityType, alias)})";
                case "CEILING": return $"CEILING({Emit(method.Args[0], entityType, alias)})";
                case "FLOOR": return $"FLOOR({Emit(method.Args[0], entityType, alias)})";
                case "ROUND" when method.Args.Count == 2: return $"ROUND({Emit(method.Args[0], entityType, alias)}, {Emit(method.Args[1], entityType, alias)})";
                case "ROUND" when method.Args.Count == 3: return $"ROUND({Emit(method.Args[0], entityType, alias)}, {Emit(method.Args[1], entityType, alias)}, {Emit(method.Args[2], entityType, alias)})";
                default: throw new NotSupportedException($"Method '{method.Method}' is not supported for SQL generation.");
            }
        }

        private string EmitLike(SqlLikeNode like, IEntityType entityType, string? alias)
        {
            var col = $"{WithAlias(alias)}{Quote(ModelBinder.GetColumnName(like.Property, entityType))}";
            var param = EmitValue(like.PatternValue);
            var op = like.Negated ? "NOT LIKE" : "LIKE";
            return $"{col} {op} {param}";
        }

        private string EmitIn(SqlInNode inNode, IEntityType entityType, string? alias)
        {
            if (inNode.Values.Count == 0)
            {
                return inNode.Negated ? "1=1" : "1=0";
            }

            var col = $"{WithAlias(alias)}{Quote(ModelBinder.GetColumnName(inNode.Property, entityType))}";
            var op = inNode.Negated ? "NOT IN" : "IN";
            // EF Core pattern: manual loop vs string.Join+Select — avoids enumerator alloc for IN lists
            var sb = new StringBuilder();
            sb.Append(col).Append(' ').Append(op).Append(" (");
            for (var i = 0; i < inNode.Values.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(EmitValue(inNode.Values[i]));
            }
            sb.Append(')');
            return sb.ToString();
        }

        private string EmitIsEmpty(SqlIsEmptyNode empty, IEntityType entityType, string? alias)
        {
            var col = $"{WithAlias(alias)}{Quote(ModelBinder.GetColumnName(empty.Property, entityType))}";
            var check = $"({col} IS NULL OR {col} = '')";
            return empty.Negated ? $"NOT {check}" : check;
        }
    }
}
