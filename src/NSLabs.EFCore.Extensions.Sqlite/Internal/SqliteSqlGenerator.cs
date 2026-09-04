using System.Text;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class SqliteSqlGenerator
{
    internal const int MaxParametersPerCommand = 999;

    public static IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParametersPerCommand, 1);

        // SAFETY S8: clamp to SQLite limit; caller may pass 2000 default
        var effectiveLimit = Math.Min(maxParametersPerCommand, MaxParametersPerCommand);

        // SAFETY S2: presizing never changes order
        var chunks = new List<SqlChunkPlan>(Math.Max(operations.Count, 4));

        foreach (var operation in operations)
        {
            if (operation.Kind == BulkOperationKind.Upsert)
            {
                ExpandUpsert(operation, chunks, effectiveLimit);
                continue;
            }

            var cost = CountParameters(operation);
            if (cost > effectiveLimit)
            {
                throw new InvalidOperationException(
                    $"Operation #{operation.GlobalIndex} requires {cost} parameters which exceeds MaxParametersPerCommand={effectiveLimit} for SQLite (SQLITE_MAX_VARIABLE_NUMBER=999). Increase the limit if your SQLite build allows more, or split the operation.");
            }

            chunks.Add(BuildStatementChunk(operation));
        }

        return chunks;
    }

    private static void ExpandUpsert(
        BoundOperation operation,
        List<SqlChunkPlan> chunks,
        int effectiveLimit)
    {
        if (operation.UpsertSpec is not { } spec)
            throw new InvalidOperationException($"Upsert operation #{operation.GlobalIndex} was not bound to an upsert spec.");

        if (spec.Rows.Count == 0)
        {
            // Zero-row upsert: emit a no-op chunk that still contributes 0 rowcount
            chunks.Add(BuildZeroRowUpsertChunk(operation));
            return;
        }

        // EF Core pattern: manual loop vs LINQ Sum
        var fixedCost = CountParameterNodes(spec.Guard);
        for (var i = 0; i < operation.Assignments.Count; i++)
        {
            var assignment = operation.Assignments[i];
            fixedCost += assignment.ValueExpression is not null ? CountParameterNodes(assignment.ValueExpression) : 1;
        }

        var perRowCost = spec.InsertColumns.Count;
        if (fixedCost + perRowCost > effectiveLimit)
        {
            throw new InvalidOperationException(
                $"Operation #{operation.GlobalIndex} requires {fixedCost + perRowCost} parameters for a single upsert row which exceeds MaxParametersPerCommand={effectiveLimit}. Increase the limit or split the operation.");
        }

        var startRow = 0;
        while (startRow < spec.Rows.Count)
        {
            var capacity = (effectiveLimit - fixedCost) / perRowCost;
            if (capacity <= 0) capacity = 1;
            var rowCount = Math.Min(spec.Rows.Count - startRow, capacity);
            chunks.Add(BuildUpsertChunk(operation, startRow, rowCount));
            startRow += rowCount;
        }
    }

    private static SqlChunkPlan BuildStatementChunk(BoundOperation operation)
    {
        var emitter = new ParameterEmitter(CountParameters(operation) + 2);
        var sb = StringBuilderCache.Acquire(180);
        try
        {
            EmitStatement(emitter, sb, operation);
            sb.Append(';');
            return new SqlChunkPlan
            {
                CommandText = StringBuilderCache.GetStringAndRelease(sb),
                Parameters = emitter.Parameters,
                OperationIndices = [operation.GlobalIndex]
            };
        }
        catch
        {
            StringBuilderCache.Release(sb);
            throw;
        }
    }

    private static SqlChunkPlan BuildUpsertChunk(BoundOperation operation, int startRow, int rowCount)
    {
        var spec = operation.UpsertSpec!;
        // Estimate params: perRow*rowCount + fixed
        var estimated = spec.InsertColumns.Count * rowCount + 4;
        var emitter = new ParameterEmitter(estimated);
        var sb = StringBuilderCache.Acquire(256 + rowCount * 32);
        try
        {
            EmitInsertOnConflict(emitter, sb, operation, startRow, rowCount);
            sb.Append(';');
            return new SqlChunkPlan
            {
                CommandText = StringBuilderCache.GetStringAndRelease(sb),
                Parameters = emitter.Parameters,
                OperationIndices = [operation.GlobalIndex]
            };
        }
        catch
        {
            StringBuilderCache.Release(sb);
            throw;
        }
    }

    private static SqlChunkPlan BuildZeroRowUpsertChunk(BoundOperation operation)
    {
        // No SQL to execute; executor will treat as 0 affected.
        return new SqlChunkPlan
        {
            CommandText = "-- zero-row upsert no-op",
            Parameters = [],
            OperationIndices = [operation.GlobalIndex]
        };
    }

    private static void EmitStatement(ParameterEmitter emitter, StringBuilder sql, BoundOperation operation)
    {
        var table = QuoteTable(operation.EntityType);

        switch (operation.Kind)
        {
            case BulkOperationKind.Update:
                if (operation.Assignments.Count == 0)
                    throw new InvalidOperationException($"Update operation #{operation.GlobalIndex} has no assignments.");
                sql.Append("UPDATE ").Append(table).Append(" SET ");
                for (var i = 0; i < operation.Assignments.Count; i++)
                {
                    if (i > 0) sql.Append(", ");
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

        sql.Append(" WHERE ").Append(EmitPredicate(emitter, operation));
    }

    private static void EmitInsertOnConflict(ParameterEmitter emitter, StringBuilder sql, BoundOperation operation, int startRow, int rowCount)
    {
        var spec = operation.UpsertSpec!;
        var entityType = operation.EntityType;
        var table = QuoteTable(entityType);

        sql.Append("INSERT INTO ").Append(table).Append(" (");
        for (var i = 0; i < spec.InsertColumns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(Quote(ModelBinder.GetColumnName(spec.InsertColumns[i], entityType)));
        }
        sql.Append(") VALUES ");

        for (var r = 0; r < rowCount; r++)
        {
            if (r > 0) sql.Append(", ");
            sql.Append('(');
            var row = spec.Rows[startRow + r];
            for (var c = 0; c < row.InsertValues.Count; c++)
            {
                if (c > 0) sql.Append(", ");
                sql.Append(emitter.EmitValue(row.InsertValues[c].Value));
            }
            sql.Append(')');
        }

        sql.Append(" ON CONFLICT (");
        for (var i = 0; i < spec.ConflictProperties.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(Quote(ModelBinder.GetColumnName(spec.ConflictProperties[i], entityType)));
        }
        sql.Append(") DO ");

        var hasMatchedUpdatePayload = operation.Assignments.Count > 0 || spec.UpdateColumns.Count > 0;
        if (!hasMatchedUpdatePayload)
        {
            sql.Append("NOTHING");
            return;
        }

        sql.Append("UPDATE SET ");

        if (operation.Assignments.Count > 0)
        {
            for (var i = 0; i < operation.Assignments.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var assignment = operation.Assignments[i];
                sql.Append(Quote(ModelBinder.GetColumnName(assignment.Property, entityType)))
                   .Append(" = ")
                   .Append(assignment.ValueExpression is not null
                       ? emitter.Emit(assignment.ValueExpression, entityType)
                       : emitter.EmitValue(assignment.Value));
            }
        }
        else
        {
            for (var i = 0; i < spec.UpdateColumns.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var col = Quote(ModelBinder.GetColumnName(spec.UpdateColumns[i], entityType));
                sql.Append(col).Append(" = excluded.").Append(col);
            }
        }

        if (spec.Guard is { } guard)
        {
            sql.Append(" WHERE ").Append(emitter.Emit(guard, entityType));
        }
    }

    private static string EmitPredicate(ParameterEmitter emitter, BoundOperation operation)
    {
        if (operation.PredicateParts.Count == 0)
            throw new InvalidOperationException($"Operation #{operation.GlobalIndex} on '{operation.EntityType.DisplayName()}' has no predicate; refusing to emit unbounded DML.");

        if (operation.PredicateParts.Count == 1)
            return emitter.Emit(operation.PredicateParts[0], operation.EntityType);

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
        var count = 0;
        for (var i = 0; i < operation.Assignments.Count; i++)
        {
            var assignment = operation.Assignments[i];
            count += assignment.ValueExpression is not null ? CountParameterNodes(assignment.ValueExpression) : 1;
        }
        foreach (var part in operation.PredicateParts)
            count += CountParameterNodes(part);
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
        var sum = 0;
        for (var i = 0; i < method.Args.Count; i++) sum += CountParameterNodes(method.Args[i]);
        return sum;
    }

    internal static string Quote(string identifier)
    {
        // SAFETY S6: sqlite quoting with ""
        if (identifier.IndexOf('"') < 0)
            return "\"" + identifier + "\"";
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    internal static string QuoteTable(IEntityType entityType)
    {
        var raw = ModelBinder.GetTableName(entityType);
        // Split on '.' for schema.table if present
        var parts = raw.Split('.');
        if (parts.Length == 1) return Quote(raw);
        var sb = new StringBuilder(raw.Length + 4);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append('.');
            sb.Append(Quote(parts[i]));
        }
        return sb.ToString();
    }

    private sealed class ParameterEmitter
    {
        private readonly List<SqlParam> _parameters;
        public IReadOnlyList<SqlParam> Parameters => _parameters;
        private int Counter { get; set; }

        public ParameterEmitter(int capacity = 8) => _parameters = new List<SqlParam>(capacity);

        public string Emit(SqlNode node, IEntityType entityType, string? alias = null) => node switch
        {
            SqlColumnNode column => Quote(ModelBinder.GetColumnName(column.Property, entityType)),
            SqlBooleanNode boolean => $"{Quote(ModelBinder.GetColumnName(boolean.Property, entityType))} = 1",
            SqlParameterNode parameter => EmitValue(parameter.Value),
            SqlNullCheckNode nullCheck => $"{Quote(ModelBinder.GetColumnName(nullCheck.Property, entityType))} {(nullCheck.IsNotNull ? "IS NOT NULL" : "IS NULL")}",
            SqlNotNode not => $"NOT ({Emit(not.Inner, entityType, alias)})",
            SqlUnaryNode unary => EmitUnary(unary, entityType, alias),
            SqlConditionalNode cond => $"CASE WHEN {Emit(cond.Test, entityType, alias)} THEN {Emit(cond.IfTrue, entityType, alias)} ELSE {Emit(cond.IfFalse, entityType, alias)} END",
            SqlCoalesceNode co => $"COALESCE({Emit(co.Left, entityType, alias)}, {Emit(co.Right, entityType, alias)})",
            SqlMethodCallNode method => EmitMethod(method, entityType, alias),
            SqlLikeNode like => EmitLike(like, entityType, alias),
            SqlInNode inNode => EmitIn(inNode, entityType, alias),
            SqlIsEmptyNode empty => EmitIsEmpty(empty, entityType, alias),
            SqlBinaryNode { Operator: SqlBinaryOperator.And or SqlBinaryOperator.Or } logical => $"({Emit(logical.Left, entityType, alias)} {(logical.Operator == SqlBinaryOperator.And ? "AND" : "OR")} {Emit(logical.Right, entityType, alias)})",
            SqlBinaryNode arithmetic when IsArithmetic(arithmetic.Operator) => EmitArithmetic(arithmetic, entityType, alias),
            SqlBinaryNode comparison => $"{Emit(comparison.Left, entityType, alias)} {RenderComparison(comparison.Operator)} {Emit(comparison.Right, entityType, alias)}",
            _ => throw new NotSupportedException($"Cannot emit node '{node.GetType().Name}'.")
        };

        public string EmitValue(object? value)
        {
            var name = $"@p{Counter++}";
            _parameters.Add(new SqlParam(name, value));
            return name;
        }

        private static bool IsArithmetic(SqlBinaryOperator op) => op is SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo;

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

        private static string RenderArithmetic(SqlBinaryOperator op) => op switch
        {
            SqlBinaryOperator.Add => "+",
            SqlBinaryOperator.Subtract => "-",
            SqlBinaryOperator.Multiply => "*",
            SqlBinaryOperator.Divide => "/",
            SqlBinaryOperator.Modulo => "%",
            _ => throw new NotSupportedException($"Operator '{op}' is not an arithmetic operator.")
        };

        private string EmitArithmetic(SqlBinaryNode node, IEntityType entityType, string? alias)
        {
            // String concat uses || in SQLite
            if (node.Operator == SqlBinaryOperator.Add && IsStringConcat(node, entityType))
            {
                return $"({Emit(node.Left, entityType, alias)} || {Emit(node.Right, entityType, alias)})";
            }
            return $"({Emit(node.Left, entityType, alias)} {RenderArithmetic(node.Operator)} {Emit(node.Right, entityType, alias)})";
        }

        private static bool IsStringConcat(SqlBinaryNode node, IEntityType entityType)
        {
            // Heuristic: if either side is string-typed column or string parameter
            if (node.Left is SqlColumnNode lc && lc.Property.ClrType == typeof(string)) return true;
            if (node.Right is SqlColumnNode rc && rc.Property.ClrType == typeof(string)) return true;
            if (node.Left is SqlParameterNode lp && lp.Value is string) return true;
            if (node.Right is SqlParameterNode rp && rp.Value is string) return true;
            // Also check method call that returns string? simplified
            return false;
        }

        private string EmitUnary(SqlUnaryNode unary, IEntityType entityType, string? alias) => unary.Operator switch
        {
            SqlUnaryOperator.Negate => $"-{Emit(unary.Inner, entityType, alias)}",
            _ => throw new NotSupportedException($"Unary operator '{unary.Operator}' is not supported.")
        };

        private string EmitMethod(SqlMethodCallNode method, IEntityType entityType, string? alias)
        {
            switch (method.Method)
            {
                case "UPPER": return $"UPPER({Emit(method.Args[0], entityType, alias)})";
                case "LOWER": return $"LOWER({Emit(method.Args[0], entityType, alias)})";
                case "TRIM": return $"TRIM({Emit(method.Args[0], entityType, alias)})";
                case "LTRIM": return $"LTRIM({Emit(method.Args[0], entityType, alias)})";
                case "RTRIM": return $"RTRIM({Emit(method.Args[0], entityType, alias)})";
                case "LEN": return $"LENGTH({Emit(method.Args[0], entityType, alias)})";
                case "SUBSTRING":
                    // SUBSTR(col, start, len) — SQLite
                    if (method.Args.Count == 3)
                        return $"SUBSTR({Emit(method.Args[0], entityType, alias)}, {Emit(method.Args[1], entityType, alias)}, {Emit(method.Args[2], entityType, alias)})";
                    return $"SUBSTR({Emit(method.Args[0], entityType, alias)}, {Emit(method.Args[1], entityType, alias)})";
                case "REPLACE": return $"REPLACE({Emit(method.Args[0], entityType, alias)}, {Emit(method.Args[1], entityType, alias)}, {Emit(method.Args[2], entityType, alias)})";
                case "CONCAT":
                {
                    if (method.Args.Count == 0) return "''";
                    if (method.Args.Count == 1) return Emit(method.Args[0], entityType, alias);
                    var sb = StringBuilderCache.Acquire(32);
                    try
                    {
                        sb.Append('(');
                        for (var i = 0; i < method.Args.Count; i++)
                        {
                            if (i > 0) sb.Append(" || ");
                            sb.Append(Emit(method.Args[i], entityType, alias));
                        }
                        sb.Append(')');
                        return StringBuilderCache.GetStringAndRelease(sb);
                    }
                    catch
                    {
                        StringBuilderCache.Release(sb);
                        throw;
                    }
                }
                case "ABS": return $"ABS({Emit(method.Args[0], entityType, alias)})";
                case "CEILING": return $"CEIL({Emit(method.Args[0], entityType, alias)})";
                case "FLOOR": return $"FLOOR({Emit(method.Args[0], entityType, alias)})";
                case "ROUND" when method.Args.Count == 1: return $"ROUND({Emit(method.Args[0], entityType, alias)})";
                case "ROUND" when method.Args.Count == 2: return $"ROUND({Emit(method.Args[0], entityType, alias)}, {Emit(method.Args[1], entityType, alias)})";
                case "ROUND" when method.Args.Count == 3:
                    // SQLite has no 3-arg ROUND for truncate; emulate via CAST truncation
                    // ROUND(x,0,1) in SQL Server means truncate; we emit CAST(x AS INTEGER)
                    return $"CAST({Emit(method.Args[0], entityType, alias)} AS INTEGER)";
                default: throw new NotSupportedException($"Method '{method.Method}' is not supported for SQLite generation.");
            }
        }

        private string EmitLike(SqlLikeNode like, IEntityType entityType, string? alias)
        {
            var col = Quote(ModelBinder.GetColumnName(like.Property, entityType));
            var raw = like.PatternValue as string ?? throw new NotSupportedException("LIKE pattern must be a string.");
            var finalPattern = BuildLikePattern(raw, like.Kind);
            var param = EmitValue(finalPattern);
            var op = like.Negated ? "NOT LIKE" : "LIKE";
            return $"{col} {op} {param} ESCAPE '\\'";
        }

        private static string BuildLikePattern(string raw, SqlLikeKind kind) => kind switch
        {
            SqlLikeKind.Contains => $"%{EscapeLikeSqlite(raw)}%",
            SqlLikeKind.StartsWith => $"{EscapeLikeSqlite(raw)}%",
            SqlLikeKind.EndsWith => $"%{EscapeLikeSqlite(raw)}",
            SqlLikeKind.Like => raw,
            _ => throw new NotSupportedException($"SqlLikeKind '{kind}' is not supported.")
        };

        private static string EscapeLikeSqlite(string pattern)
        {
            if (pattern.IndexOf('\\') < 0 && pattern.IndexOf('%') < 0 && pattern.IndexOf('_') < 0)
                return pattern;
            var sb = StringBuilderCache.Acquire(pattern.Length + 8);
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '\\') sb.Append("\\\\");
                else if (c == '%') sb.Append("\\%");
                else if (c == '_') sb.Append("\\_");
                else sb.Append(c);
            }
            return StringBuilderCache.GetStringAndRelease(sb);
        }

        private string EmitIn(SqlInNode inNode, IEntityType entityType, string? alias)
        {
            if (inNode.Values.Count == 0) return inNode.Negated ? "1=1" : "1=0";
            var col = Quote(ModelBinder.GetColumnName(inNode.Property, entityType));
            var op = inNode.Negated ? "NOT IN" : "IN";
            var sb = StringBuilderCache.Acquire(32);
            try
            {
                sb.Append(col).Append(' ').Append(op).Append(" (");
                for (var i = 0; i < inNode.Values.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(EmitValue(inNode.Values[i]));
                }
                sb.Append(')');
                return StringBuilderCache.GetStringAndRelease(sb);
            }
            catch
            {
                StringBuilderCache.Release(sb);
                throw;
            }
        }

        private string EmitIsEmpty(SqlIsEmptyNode empty, IEntityType entityType, string? alias)
        {
            var col = Quote(ModelBinder.GetColumnName(empty.Property, entityType));
            var check = $"({col} IS NULL OR {col} = '')";
            return empty.Negated ? $"NOT {check}" : check;
        }
    }
}
