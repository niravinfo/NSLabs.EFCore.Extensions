using System.Text;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class NpgsqlSqlGenerator
{
    internal const int MaxParametersPerCommand = 65535;

    public static IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParametersPerCommand, 1);

        // Clamp to PostgreSQL wire-protocol limit; caller may pass the 2000 default.
        // Callers with large batches should raise MaxParametersPerCommand (e.g. 20000-33000).
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
                    $"Operation #{operation.GlobalIndex} requires {cost} parameters which exceeds MaxParametersPerCommand={effectiveLimit} for PostgreSQL (protocol limit 65535). Increase the limit or split the operation.");
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
            var plan = new SqlChunkPlan
            {
                CommandText = StringBuilderCache.GetStringAndRelease(sb),
                Parameters = emitter.Parameters,
                OperationIndices = [operation.GlobalIndex]
            };
            return plan;
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
            var plan = new SqlChunkPlan
            {
                CommandText = StringBuilderCache.GetStringAndRelease(sb),
                Parameters = emitter.Parameters,
                OperationIndices = [operation.GlobalIndex]
            };
            return plan;
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
        var plan = new SqlChunkPlan
        {
            CommandText = "-- zero-row upsert no-op",
            Parameters = [],
            OperationIndices = [operation.GlobalIndex]
        };
        return plan;
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
                       .Append(" = ");

                    if (assignment.ValueExpression is not null)
                    {
                        emitter.Emit(sql, assignment.ValueExpression, operation.EntityType);
                    }
                    else
                    {
                        emitter.EmitValue(sql, assignment.Value);
                    }
                }
                break;

            case BulkOperationKind.Delete:
                sql.Append("DELETE FROM ").Append(table);
                break;

            default:
                throw new NotSupportedException($"Operation kind '{operation.Kind}' cannot be emitted as a simple statement.");
        }

        sql.Append(" WHERE ");
        EmitPredicate(emitter, sql, operation);
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
                emitter.EmitValue(sql, row.InsertValues[c].Value);
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
                // LHS is always the bare column name: PG syntax is SET column_name = ...
                sql.Append(Quote(ModelBinder.GetColumnName(assignment.Property, entityType)))
                   .Append(" = ");

                if (assignment.ValueExpression is not null)
                {
                    // Computed RHS references the target row -> qualify as "Table"."Col"
                    emitter.EmitQualified(sql, assignment.ValueExpression, entityType);
                }
                else
                {
                    emitter.EmitValue(sql, assignment.Value);
                }
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
            // PG DO UPDATE WHERE must qualify target columns as "Table"."Col" (never bare).
            sql.Append(" WHERE ");
            emitter.EmitQualified(sql, guard, entityType);
        }
    }

    private static void EmitPredicate(ParameterEmitter emitter, StringBuilder sql, BoundOperation operation)
    {
        if (operation.PredicateParts.Count == 0)
            throw new InvalidOperationException($"Operation #{operation.GlobalIndex} on '{operation.EntityType.DisplayName()}' has no predicate; refusing to emit unbounded DML.");

        if (operation.PredicateParts.Count == 1)
        {
            emitter.Emit(sql, operation.PredicateParts[0], operation.EntityType);
            return;
        }

        for (var i = 0; i < operation.PredicateParts.Count; i++)
        {
            if (i > 0) sql.Append(" AND ");
            emitter.Emit(sql, operation.PredicateParts[i], operation.EntityType);
        }
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
        // Always-ANY: one array param per IN (0 for empty/all-null shapes).
        // byte[]-element lists stay multi-param (v1) and cost per non-null value.
        SqlInNode inNode => LargeListHelper.NonNullCount(inNode.Values) == 0
            ? 0
            : LargeListHelper.IsByteArrayElementList(inNode.Values)
                ? LargeListHelper.NonNullCount(inNode.Values)
                : 1,
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

    internal static string QualifiedColumn(IProperty property, IEntityType entityType)
    {
        // "Table"."Col" using the short (unqualified) table name so that
        // schema-qualified mappings still emit valid PG ("Items"."Col", not "public.Items"."Col").
        var raw = ModelBinder.GetTableName(entityType);
        var dot = raw.LastIndexOf('.');
        var shortTable = dot >= 0 ? raw.Substring(dot + 1) : raw;
        return Quote(shortTable) + "." + Quote(ModelBinder.GetColumnName(property, entityType));
    }

    private sealed class ParameterEmitter
    {
        private readonly List<SqlParam> _parameters;
        public IReadOnlyList<SqlParam> Parameters => _parameters;
        private int Counter { get; set; }

        public ParameterEmitter(int capacity = 8) => _parameters = new List<SqlParam>(capacity);

        // P4: direct emission — every branch appends into the chunk's StringBuilder instead of
        // building an intermediate expression string. Emission order is unchanged, so parameter
        // numbering (@p0, @p1, ...) and generated SQL stay byte-identical.
        public void Emit(StringBuilder sql, SqlNode node, IEntityType entityType)
            => EmitInner(sql, node, entityType, qualifyTarget: false);

        public void EmitQualified(StringBuilder sql, SqlNode node, IEntityType entityType)
            => EmitInner(sql, node, entityType, qualifyTarget: true);

        private void EmitInner(StringBuilder sql, SqlNode node, IEntityType entityType, bool qualifyTarget)
        {
            switch (node)
            {
                case SqlColumnNode column:
                    AppendColumn(sql, column.Property, entityType, qualifyTarget);
                    break;

                // PG boolean: = TRUE (never = 1)
                case SqlBooleanNode boolean:
                    AppendColumn(sql, boolean.Property, entityType, qualifyTarget);
                    sql.Append(" = TRUE");
                    break;

                case SqlParameterNode parameter:
                    EmitValue(sql, parameter.Value);
                    break;

                case SqlNullCheckNode nullCheck:
                    AppendColumn(sql, nullCheck.Property, entityType, qualifyTarget);
                    sql.Append(nullCheck.IsNotNull ? " IS NOT NULL" : " IS NULL");
                    break;

                case SqlNotNode not:
                    EmitNot(sql, not, entityType, qualifyTarget);
                    break;

                case SqlUnaryNode unary:
                    EmitUnary(sql, unary, entityType, qualifyTarget);
                    break;

                case SqlConditionalNode cond:
                    sql.Append("CASE WHEN ");
                    EmitInner(sql, cond.Test, entityType, qualifyTarget);
                    sql.Append(" THEN ");
                    EmitInner(sql, cond.IfTrue, entityType, qualifyTarget);
                    sql.Append(" ELSE ");
                    EmitInner(sql, cond.IfFalse, entityType, qualifyTarget);
                    sql.Append(" END");
                    break;

                case SqlCoalesceNode co:
                    sql.Append("COALESCE(");
                    EmitInner(sql, co.Left, entityType, qualifyTarget);
                    sql.Append(", ");
                    EmitInner(sql, co.Right, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case SqlMethodCallNode method:
                    EmitMethod(sql, method, entityType, qualifyTarget);
                    break;

                case SqlLikeNode like:
                    EmitLike(sql, like, entityType, qualifyTarget);
                    break;

                case SqlInNode inNode:
                    EmitIn(sql, inNode, entityType, qualifyTarget);
                    break;

                case SqlIsEmptyNode empty:
                    EmitIsEmpty(sql, empty, entityType, qualifyTarget);
                    break;

                case SqlBinaryNode { Operator: SqlBinaryOperator.And or SqlBinaryOperator.Or } logical:
                    sql.Append('(');
                    EmitInner(sql, logical.Left, entityType, qualifyTarget);
                    sql.Append(logical.Operator == SqlBinaryOperator.And ? " AND " : " OR ");
                    EmitInner(sql, logical.Right, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case SqlBinaryNode arithmetic when IsArithmetic(arithmetic.Operator):
                    EmitArithmetic(sql, arithmetic, entityType, qualifyTarget);
                    break;

                case SqlBinaryNode comparison:
                    EmitInner(sql, comparison.Left, entityType, qualifyTarget);
                    sql.Append(' ').Append(RenderComparison(comparison.Operator)).Append(' ');
                    EmitInner(sql, comparison.Right, entityType, qualifyTarget);
                    break;

                default:
                    throw new NotSupportedException($"Cannot emit node '{node.GetType().Name}'.");
            }
        }

        public void EmitValue(StringBuilder sql, object? value)
        {
            // The name string is still required by the returned SqlParam; only the surrounding
            // intermediate expression strings are gone.
            var name = $"@p{Counter++}";
            _parameters.Add(new SqlParam(name, value));
            sql.Append(name);
        }

        private static void AppendColumn(StringBuilder sql, IProperty property, IEntityType entityType, bool qualifyTarget)
        {
            if (qualifyTarget)
            {
                sql.Append(QualifiedColumn(property, entityType));
            }
            else
            {
                sql.Append(Quote(ModelBinder.GetColumnName(property, entityType)));
            }
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

        private void EmitArithmetic(StringBuilder sql, SqlBinaryNode node, IEntityType entityType, bool qualifyTarget)
        {
            // String concat uses || in PostgreSQL
            var stringConcat = node.Operator == SqlBinaryOperator.Add && IsStringConcat(node);

            sql.Append('(');
            EmitInner(sql, node.Left, entityType, qualifyTarget);
            if (stringConcat)
            {
                sql.Append(" || ");
            }
            else
            {
                sql.Append(' ').Append(RenderArithmetic(node.Operator)).Append(' ');
            }

            EmitInner(sql, node.Right, entityType, qualifyTarget);
            sql.Append(')');
        }

        private static bool IsStringConcat(SqlBinaryNode node)
        {
            // Heuristic: if either side is string-typed column or string parameter
            if (node.Left is SqlColumnNode lc && lc.Property.ClrType == typeof(string)) return true;
            if (node.Right is SqlColumnNode rc && rc.Property.ClrType == typeof(string)) return true;
            if (node.Left is SqlParameterNode lp && lp.Value is string) return true;
            if (node.Right is SqlParameterNode rp && rp.Value is string) return true;
            // Also check method call that returns string? simplified
            return false;
        }

        private void EmitUnary(StringBuilder sql, SqlUnaryNode unary, IEntityType entityType, bool qualifyTarget)
        {
            switch (unary.Operator)
            {
                case SqlUnaryOperator.Negate:
                    sql.Append('-');
                    EmitInner(sql, unary.Inner, entityType, qualifyTarget);
                    break;

                default:
                    throw new NotSupportedException($"Unary operator '{unary.Operator}' is not supported.");
            }
        }

        // Emits the argument at a fixed position (extra arguments are ignored, a missing one
        // throws the same index exception the string-returning emitter threw) — matches the
        // pre-P4 fixed-arity emission exactly.
        private void EmitArg(StringBuilder sql, SqlMethodCallNode method, int index, IEntityType entityType, bool qualifyTarget)
            => EmitInner(sql, method.Args[index], entityType, qualifyTarget);

        // Emits every argument joined with ", " — for arities already pinned by a `when` guard.
        private void EmitArgs(StringBuilder sql, SqlMethodCallNode method, IEntityType entityType, bool qualifyTarget)
        {
            for (var i = 0; i < method.Args.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                EmitInner(sql, method.Args[i], entityType, qualifyTarget);
            }
        }

        private void EmitMethod(StringBuilder sql, SqlMethodCallNode method, IEntityType entityType, bool qualifyTarget)
        {
            switch (method.Method)
            {
                case "UPPER":
                    sql.Append("UPPER(");
                    EmitArg(sql, method, 0, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "LOWER":
                    sql.Append("LOWER(");
                    EmitArg(sql, method, 0, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "TRIM":
                    sql.Append("TRIM(");
                    EmitArg(sql, method, 0, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "LTRIM":
                    sql.Append("LTRIM(");
                    EmitArg(sql, method, 0, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "RTRIM":
                    sql.Append("RTRIM(");
                    EmitArg(sql, method, 0, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "LEN":
                    sql.Append("CHAR_LENGTH(");
                    EmitArg(sql, method, 0, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "SUBSTRING" when method.Args.Count == 3:
                    // PostgreSQL: SUBSTRING(x FROM s FOR n)
                    sql.Append("SUBSTRING(");
                    EmitInner(sql, method.Args[0], entityType, qualifyTarget);
                    sql.Append(" FROM ");
                    EmitInner(sql, method.Args[1], entityType, qualifyTarget);
                    sql.Append(" FOR ");
                    EmitInner(sql, method.Args[2], entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "SUBSTRING":
                    // PostgreSQL: SUBSTRING(x FROM s)
                    sql.Append("SUBSTRING(");
                    EmitInner(sql, method.Args[0], entityType, qualifyTarget);
                    sql.Append(" FROM ");
                    EmitInner(sql, method.Args[1], entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "REPLACE":
                    sql.Append("REPLACE(");
                    EmitArg(sql, method, 0, entityType, qualifyTarget);
                    sql.Append(", ");
                    EmitArg(sql, method, 1, entityType, qualifyTarget);
                    sql.Append(", ");
                    EmitArg(sql, method, 2, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "CONCAT":
                {
                    if (method.Args.Count == 0)
                    {
                        sql.Append("''");
                        break;
                    }

                    if (method.Args.Count == 1)
                    {
                        EmitInner(sql, method.Args[0], entityType, qualifyTarget);
                        break;
                    }

                    sql.Append('(');
                    for (var i = 0; i < method.Args.Count; i++)
                    {
                        if (i > 0) sql.Append(" || ");
                        EmitInner(sql, method.Args[i], entityType, qualifyTarget);
                    }

                    sql.Append(')');
                    break;
                }

                case "ABS":
                    sql.Append("ABS(");
                    EmitArg(sql, method, 0, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "CEILING":
                    sql.Append("CEIL(");
                    EmitArg(sql, method, 0, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "FLOOR":
                    sql.Append("FLOOR(");
                    EmitArg(sql, method, 0, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "ROUND" when method.Args.Count == 1:
                    sql.Append("ROUND(");
                    EmitArgs(sql, method, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "ROUND" when method.Args.Count == 2:
                    sql.Append("ROUND(");
                    EmitArgs(sql, method, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "ROUND" when method.Args.Count == 3:
                    // SQL Server ROUND(x, digits, 1) means truncate; PG equivalent is TRUNC(x, digits).
                    // Third arg is the truncate-flag; ignore its value (never emitted).
                    sql.Append("TRUNC(");
                    EmitInner(sql, method.Args[0], entityType, qualifyTarget);
                    sql.Append(", ");
                    EmitInner(sql, method.Args[1], entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "LEAST" when method.Args.Count == 2:
                    sql.Append("LEAST(");
                    EmitArgs(sql, method, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                case "GREATEST" when method.Args.Count == 2:
                    sql.Append("GREATEST(");
                    EmitArgs(sql, method, entityType, qualifyTarget);
                    sql.Append(')');
                    break;

                default:
                    throw new NotSupportedException($"Method '{method.Method}' is not supported for PostgreSQL generation.");
            }
        }

        private void EmitLike(StringBuilder sql, SqlLikeNode like, IEntityType entityType, bool qualifyTarget)
        {
            AppendColumn(sql, like.Property, entityType, qualifyTarget);

            var raw = like.PatternValue as string ?? throw new NotSupportedException("LIKE pattern must be a string.");
            var finalPattern = BuildLikePattern(raw, like.Kind);
            sql.Append(like.Negated ? " NOT LIKE " : " LIKE ");
            EmitValue(sql, finalPattern);
            sql.Append(" ESCAPE '\\'");
        }

        private static string BuildLikePattern(string raw, SqlLikeKind kind) => kind switch
        {
            SqlLikeKind.Contains => $"%{EscapeLike(raw)}%",
            SqlLikeKind.StartsWith => $"{EscapeLike(raw)}%",
            SqlLikeKind.EndsWith => $"%{EscapeLike(raw)}",
            SqlLikeKind.Like => raw,
            _ => throw new NotSupportedException($"SqlLikeKind '{kind}' is not supported.")
        };

        private static string EscapeLike(string pattern)
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

        // Negation of a bare Contains distributes per EF10 (§4.1) instead of wrapping
        // in NOT (...): the wrapper is wrong for nullable columns in both directions,
        // and a <> ALL rewrite would diverge from = ANY semantics. Only a NOT whose
        // direct inner is an IN distributes; deeper NOTs keep the wrapper.
        private void EmitNot(StringBuilder sql, SqlNotNode not, IEntityType entityType, bool qualifyTarget)
        {
            if (not.Inner is SqlInNode inNode)
            {
                EmitInNegated(sql, inNode, entityType, qualifyTarget);
                return;
            }

            sql.Append("NOT (");
            EmitInner(sql, not.Inner, entityType, qualifyTarget);
            sql.Append(')');
        }

        private string Column(SqlInNode inNode, IEntityType entityType, bool qualifyTarget)
            => qualifyTarget
                ? QualifiedColumn(inNode.Property, entityType)
                : Quote(ModelBinder.GetColumnName(inNode.Property, entityType));

        private void EmitIn(StringBuilder sql, SqlInNode inNode, IEntityType entityType, bool qualifyTarget)
        {
            // The translator never sets SqlInNode.Negated (negation arrives as SqlNotNode);
            // honor it anyway so both spellings agree.
            if (inNode.Negated)
            {
                EmitInNegated(sql, inNode, entityType, qualifyTarget);
                return;
            }

            var (nonNulls, hasNull) = LargeListHelper.PartitionNulls(inNode.Values);
            var col = Column(inNode, entityType, qualifyTarget);

            if (nonNulls.Count == 0)
            {
                if (hasNull)
                {
                    sql.Append(col).Append(" IS NULL");
                }
                else
                {
                    sql.Append("1=0");
                }

                return;
            }

            // OR-expansions are parenthesized: predicate parts are AND-joined bare,
            // so a bare "... OR ... IS NULL" would misbind to its neighbors. The opening
            // paren is written before the core because the destination is the final buffer.
            var parenthesize = hasNull && inNode.Property.IsNullable;
            if (parenthesize)
            {
                sql.Append('(');
            }

            // v1 byte[]-element lists stay multi-param (base64 does not round-trip);
            // everything else is one typed array param (always-ANY, §4.3).
            if (LargeListHelper.IsByteArrayElementList(inNode.Values))
            {
                sql.Append(col).Append(" IN (");
                for (var i = 0; i < nonNulls.Count; i++)
                {
                    if (i > 0) sql.Append(", ");
                    EmitValue(sql, nonNulls[i]);
                }

                sql.Append(')');
            }
            else
            {
                sql.Append(col).Append(" = ANY (");
                EmitValue(sql, NpgsqlLargeList.BuildTypedArray(inNode.Property, nonNulls));
                sql.Append(')');
            }

            if (parenthesize)
            {
                sql.Append(" OR ").Append(col).Append(" IS NULL)");
            }
        }

        private void EmitInNegated(StringBuilder sql, SqlInNode inNode, IEntityType entityType, bool qualifyTarget)
        {
            var (nonNulls, hasNull) = LargeListHelper.PartitionNulls(inNode.Values);
            var col = Column(inNode, entityType, qualifyTarget);

            if (nonNulls.Count == 0)
            {
                if (hasNull)
                {
                    sql.Append(col).Append(" IS NOT NULL");
                }
                else
                {
                    sql.Append("1=1");
                }

                return;
            }

            // Same leading-paren trick as EmitIn: the OR-expansion wraps the core,
            // so its '(' has to land in the buffer before the core is emitted.
            var andNullExpansion = hasNull && inNode.Property.IsNullable;
            var orExpansion = !hasNull && inNode.Property.IsNullable;
            if (orExpansion)
            {
                sql.Append('(');
            }

            if (LargeListHelper.IsByteArrayElementList(inNode.Values))
            {
                sql.Append(col).Append(" NOT IN (");
                for (var i = 0; i < nonNulls.Count; i++)
                {
                    if (i > 0) sql.Append(", ");
                    EmitValue(sql, nonNulls[i]);
                }

                sql.Append(')');
            }
            else
            {
                sql.Append("NOT (").Append(col).Append(" = ANY (");
                EmitValue(sql, NpgsqlLargeList.BuildTypedArray(inNode.Property, nonNulls));
                sql.Append("))");
            }

            if (andNullExpansion)
            {
                sql.Append(" AND ").Append(col).Append(" IS NOT NULL");
            }
            else if (orExpansion)
            {
                sql.Append(" OR ").Append(col).Append(" IS NULL)");
            }
        }

        private void EmitIsEmpty(StringBuilder sql, SqlIsEmptyNode empty, IEntityType entityType, bool qualifyTarget)
        {
            var col = qualifyTarget
                ? QualifiedColumn(empty.Property, entityType)
                : Quote(ModelBinder.GetColumnName(empty.Property, entityType));

            if (empty.Negated)
            {
                sql.Append("NOT ");
            }

            sql.Append('(').Append(col).Append(" IS NULL OR ").Append(col).Append(" = '')");
        }
    }
}
