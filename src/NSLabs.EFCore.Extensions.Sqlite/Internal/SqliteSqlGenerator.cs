using System.Text;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class SqliteSqlGenerator
{
    internal const int MaxParametersPerCommand = 999;

    // Large-IN mark (docs/DESIGN.md "Large IN lists"): lists above this many non-null
    // values use the single-param json_each path. Internal const in v1 — no public option.
    internal const int LargeListThreshold = 50;

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

            // Fast-aware cost: large IN lists collapse to 1 param before the budget check,
            // which is what makes the 999 clamp harmless for large lists.
            var opOverBudget = LargeListHelper.SlowOpTotal(operation) > effectiveLimit;
            var cost = CountDecidedTotal(operation, opOverBudget);
            if (cost > effectiveLimit)
            {
                throw new InvalidOperationException(
                    $"Operation #{operation.GlobalIndex} requires {cost} parameters which exceeds MaxParametersPerCommand={effectiveLimit} for SQLite (SQLITE_MAX_VARIABLE_NUMBER=999). Increase the limit if your SQLite build allows more, or split the operation.");
            }

            chunks.Add(BuildStatementChunk(operation, opOverBudget));
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

        var perRowCost = spec.InsertColumns.Count;

        // Fast-aware guard cost (§4.1): the guard's IN nodes decide against the
        // worst-case single-row unit total, so a large-IN guard collapses to 1 param here.
        var guardOverBudget =
            LargeListHelper.SlowGuardUnitTotal(spec.Guard, operation.Assignments, perRowCost) > effectiveLimit;

        // EF Core pattern: manual loop vs LINQ Sum
        var fixedCost = CountDecidedNode(spec.Guard, guardOverBudget);
        for (var i = 0; i < operation.Assignments.Count; i++)
        {
            var assignment = operation.Assignments[i];
            fixedCost += assignment.ValueExpression is not null ? CountDecidedNode(assignment.ValueExpression, guardOverBudget) : 1;
        }

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
            chunks.Add(BuildUpsertChunk(operation, startRow, rowCount, guardOverBudget));
            startRow += rowCount;
        }
    }

    private static SqlChunkPlan BuildStatementChunk(BoundOperation operation, bool opOverBudget)
    {
        var emitter = new ParameterEmitter(CountDecidedTotal(operation, opOverBudget) + 2);
        emitter.BeginOperation(opOverBudget, guardOverBudget: false);
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

    private static SqlChunkPlan BuildUpsertChunk(BoundOperation operation, int startRow, int rowCount, bool guardOverBudget)
    {
        var spec = operation.UpsertSpec!;
        // Estimate params: perRow*rowCount + fixed
        var estimated = spec.InsertColumns.Count * rowCount + 4;
        var emitter = new ParameterEmitter(estimated);
        emitter.BeginOperation(guardOverBudget, guardOverBudget);
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
                sql.Append(Quote(ModelBinder.GetColumnName(assignment.Property, entityType)))
                   .Append(" = ");

                if (assignment.ValueExpression is not null)
                {
                    emitter.Emit(sql, assignment.ValueExpression, entityType);
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
            sql.Append(" WHERE ");
            emitter.EmitGuard(sql, guard, entityType);
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

    // Fast-aware total: IN nodes cost 1 on the fast path, nonNullCount otherwise
    // (shared LargeListHelper rule — same pure rule the emitter uses, so plans and params agree).
    private static int CountDecidedTotal(BoundOperation operation, bool opOverBudget)
    {
        var count = 0;
        for (var i = 0; i < operation.Assignments.Count; i++)
        {
            var assignment = operation.Assignments[i];
            count += assignment.ValueExpression is not null ? CountDecidedNode(assignment.ValueExpression, opOverBudget) : 1;
        }
        foreach (var part in operation.PredicateParts)
            count += CountDecidedNode(part, opOverBudget);
        return count;
    }

    private static int CountDecidedNode(SqlNode? node, bool opOverBudget) => node switch
    {
        null => 0,
        SqlParameterNode => 1,
        SqlBinaryNode binary => CountDecidedNode(binary.Left, opOverBudget) + CountDecidedNode(binary.Right, opOverBudget),
        SqlNotNode not => CountDecidedNode(not.Inner, opOverBudget),
        SqlUnaryNode unary => CountDecidedNode(unary.Inner, opOverBudget),
        SqlConditionalNode cond => CountDecidedNode(cond.Test, opOverBudget) + CountDecidedNode(cond.IfTrue, opOverBudget) + CountDecidedNode(cond.IfFalse, opOverBudget),
        SqlCoalesceNode co => CountDecidedNode(co.Left, opOverBudget) + CountDecidedNode(co.Right, opOverBudget),
        SqlMethodCallNode method => CountDecidedMethodArgs(method, opOverBudget),
        SqlColumnNode => 0,
        SqlBooleanNode => 0,
        SqlNullCheckNode => 0,
        SqlLikeNode => 1,
        SqlInNode inNode => LargeListHelper.DecidedInCost(inNode, LargeListThreshold, opOverBudget),
        SqlIsEmptyNode => 0,
        _ => 0
    };

    private static int CountDecidedMethodArgs(SqlMethodCallNode method, bool opOverBudget)
    {
        var sum = 0;
        for (var i = 0; i < method.Args.Count; i++) sum += CountDecidedNode(method.Args[i], opOverBudget);
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

        // Per-operation IN decision flags (set by BeginOperation per emitted op).
        // _inGuard selects the guard-unit flag while an upsert guard is emitting.
        private bool _opOverBudget;

        private bool _guardOverBudget;

        private bool _inGuard;

        public ParameterEmitter(int capacity = 8) => _parameters = new List<SqlParam>(capacity);

        public void BeginOperation(bool opOverBudget, bool guardOverBudget)
        {
            _opOverBudget = opOverBudget;
            _guardOverBudget = guardOverBudget;
            _inGuard = false;
        }

        public void EmitGuard(StringBuilder sql, SqlNode node, IEntityType entityType)
        {
            _inGuard = true;
            try
            {
                Emit(sql, node, entityType);
            }
            finally
            {
                _inGuard = false;
            }
        }

        // P4: direct emission — every branch appends into the chunk's StringBuilder instead of
        // building an intermediate expression string. Emission order is unchanged, so parameter
        // numbering (@p0, @p1, ...) and generated SQL stay byte-identical.
        public void Emit(StringBuilder sql, SqlNode node, IEntityType entityType, string? alias = null)
        {
            switch (node)
            {
                case SqlColumnNode column:
                    sql.Append(Quote(ModelBinder.GetColumnName(column.Property, entityType)));
                    break;

                case SqlBooleanNode boolean:
                    sql.Append(Quote(ModelBinder.GetColumnName(boolean.Property, entityType))).Append(" = 1");
                    break;

                case SqlParameterNode parameter:
                    EmitValue(sql, parameter.Value);
                    break;

                case SqlNullCheckNode nullCheck:
                    sql.Append(Quote(ModelBinder.GetColumnName(nullCheck.Property, entityType)))
                        .Append(nullCheck.IsNotNull ? " IS NOT NULL" : " IS NULL");
                    break;

                case SqlNotNode not:
                    EmitNot(sql, not, entityType, alias);
                    break;

                case SqlUnaryNode unary:
                    EmitUnary(sql, unary, entityType, alias);
                    break;

                case SqlConditionalNode cond:
                    sql.Append("CASE WHEN ");
                    Emit(sql, cond.Test, entityType, alias);
                    sql.Append(" THEN ");
                    Emit(sql, cond.IfTrue, entityType, alias);
                    sql.Append(" ELSE ");
                    Emit(sql, cond.IfFalse, entityType, alias);
                    sql.Append(" END");
                    break;

                case SqlCoalesceNode co:
                    sql.Append("COALESCE(");
                    Emit(sql, co.Left, entityType, alias);
                    sql.Append(", ");
                    Emit(sql, co.Right, entityType, alias);
                    sql.Append(')');
                    break;

                case SqlMethodCallNode method:
                    EmitMethod(sql, method, entityType, alias);
                    break;

                case SqlLikeNode like:
                    EmitLike(sql, like, entityType, alias);
                    break;

                case SqlInNode inNode:
                    EmitIn(sql, inNode, entityType, alias);
                    break;

                case SqlIsEmptyNode empty:
                    EmitIsEmpty(sql, empty, entityType, alias);
                    break;

                case SqlBinaryNode { Operator: SqlBinaryOperator.And or SqlBinaryOperator.Or } logical:
                    sql.Append('(');
                    Emit(sql, logical.Left, entityType, alias);
                    sql.Append(logical.Operator == SqlBinaryOperator.And ? " AND " : " OR ");
                    Emit(sql, logical.Right, entityType, alias);
                    sql.Append(')');
                    break;

                case SqlBinaryNode arithmetic when IsArithmetic(arithmetic.Operator):
                    EmitArithmetic(sql, arithmetic, entityType, alias);
                    break;

                case SqlBinaryNode comparison:
                    Emit(sql, comparison.Left, entityType, alias);
                    sql.Append(' ').Append(RenderComparison(comparison.Operator)).Append(' ');
                    Emit(sql, comparison.Right, entityType, alias);
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

        private void EmitArithmetic(StringBuilder sql, SqlBinaryNode node, IEntityType entityType, string? alias)
        {
            // String concat uses || in SQLite
            var stringConcat = node.Operator == SqlBinaryOperator.Add && IsStringConcat(node, entityType);

            sql.Append('(');
            Emit(sql, node.Left, entityType, alias);
            if (stringConcat)
            {
                sql.Append(" || ");
            }
            else
            {
                sql.Append(' ').Append(RenderArithmetic(node.Operator)).Append(' ');
            }

            Emit(sql, node.Right, entityType, alias);
            sql.Append(')');
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

        private void EmitUnary(StringBuilder sql, SqlUnaryNode unary, IEntityType entityType, string? alias)
        {
            switch (unary.Operator)
            {
                case SqlUnaryOperator.Negate:
                    sql.Append('-');
                    Emit(sql, unary.Inner, entityType, alias);
                    break;

                default:
                    throw new NotSupportedException($"Unary operator '{unary.Operator}' is not supported.");
            }
        }

        // Emits the argument at a fixed position (extra arguments are ignored, a missing one
        // throws the same index exception the string-returning emitter threw) — matches the
        // pre-P4 fixed-arity emission exactly.
        private void EmitArg(StringBuilder sql, SqlMethodCallNode method, int index, IEntityType entityType, string? alias)
            => Emit(sql, method.Args[index], entityType, alias);

        // Emits every argument joined with ", " — for arities already pinned by a `when` guard.
        private void EmitArgs(StringBuilder sql, SqlMethodCallNode method, IEntityType entityType, string? alias)
        {
            for (var i = 0; i < method.Args.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                Emit(sql, method.Args[i], entityType, alias);
            }
        }

        private void EmitMethod(StringBuilder sql, SqlMethodCallNode method, IEntityType entityType, string? alias)
        {
            switch (method.Method)
            {
                case "UPPER":
                    sql.Append("UPPER(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "LOWER":
                    sql.Append("LOWER(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "TRIM":
                    sql.Append("TRIM(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "LTRIM":
                    sql.Append("LTRIM(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "RTRIM":
                    sql.Append("RTRIM(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "LEN":
                    sql.Append("LENGTH(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "SUBSTRING":
                    // SUBSTR(col, start, len) — SQLite. Two arguments when the count is not 3,
                    // exactly like the pre-P4 emitter (including its index-out-of-range failure).
                    sql.Append("SUBSTR(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(", ");
                    EmitArg(sql, method, 1, entityType, alias);
                    if (method.Args.Count == 3)
                    {
                        sql.Append(", ");
                        EmitArg(sql, method, 2, entityType, alias);
                    }

                    sql.Append(')');
                    break;

                case "REPLACE":
                    sql.Append("REPLACE(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(", ");
                    EmitArg(sql, method, 1, entityType, alias);
                    sql.Append(", ");
                    EmitArg(sql, method, 2, entityType, alias);
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
                        Emit(sql, method.Args[0], entityType, alias);
                        break;
                    }

                    sql.Append('(');
                    for (var i = 0; i < method.Args.Count; i++)
                    {
                        if (i > 0) sql.Append(" || ");
                        Emit(sql, method.Args[i], entityType, alias);
                    }

                    sql.Append(')');
                    break;
                }

                case "ABS":
                    sql.Append("ABS(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "CEILING":
                    sql.Append("CEIL(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "FLOOR":
                    sql.Append("FLOOR(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "ROUND" when method.Args.Count == 1:
                case "ROUND" when method.Args.Count == 2:
                    sql.Append("ROUND(");
                    EmitArgs(sql, method, entityType, alias);
                    sql.Append(')');
                    break;

                case "ROUND" when method.Args.Count == 3:
                    // SQLite has no 3-arg ROUND for truncate; emulate via CAST truncation
                    // ROUND(x,0,1) in SQL Server means truncate; we emit CAST(x AS INTEGER)
                    sql.Append("CAST(");
                    Emit(sql, method.Args[0], entityType, alias);
                    sql.Append(" AS INTEGER)");
                    break;

                case "LEAST" when method.Args.Count == 2:
                    sql.Append("MIN(");
                    EmitArgs(sql, method, entityType, alias);
                    sql.Append(')');
                    break;

                case "GREATEST" when method.Args.Count == 2:
                    sql.Append("MAX(");
                    EmitArgs(sql, method, entityType, alias);
                    sql.Append(')');
                    break;

                default:
                    throw new NotSupportedException($"Method '{method.Method}' is not supported for SQLite generation.");
            }
        }

        private void EmitLike(StringBuilder sql, SqlLikeNode like, IEntityType entityType, string? alias)
        {
            sql.Append(Quote(ModelBinder.GetColumnName(like.Property, entityType)));

            var raw = like.PatternValue as string ?? throw new NotSupportedException("LIKE pattern must be a string.");
            var finalPattern = BuildLikePattern(raw, like.Kind);
            sql.Append(like.Negated ? " NOT LIKE " : " LIKE ");
            EmitValue(sql, finalPattern);
            sql.Append(" ESCAPE '\\'");
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

        // Negation of a bare Contains distributes per EF10 (§4.1) instead of wrapping
        // in NOT (...): the wrapper is wrong for nullable columns in both directions.
        // Only a NOT whose direct inner is an IN distributes; deeper NOTs keep the wrapper.
        private void EmitNot(StringBuilder sql, SqlNotNode not, IEntityType entityType, string? alias)
        {
            if (not.Inner is SqlInNode inNode)
            {
                EmitInNegated(sql, inNode, entityType, alias);
                return;
            }

            sql.Append("NOT (");
            Emit(sql, not.Inner, entityType, alias);
            sql.Append(')');
        }

        private bool UseFastIn(SqlInNode inNode, int nonNullCount)
        {
            var flippable = !LargeListHelper.IsByteArrayElementList(inNode.Values);
            var opOverBudget = _inGuard ? _guardOverBudget : _opOverBudget;
            return LargeListHelper.UseFastInPath(nonNullCount, flippable, LargeListThreshold, opOverBudget);
        }

        private void EmitIn(StringBuilder sql, SqlInNode inNode, IEntityType entityType, string? alias)
        {
            // The translator never sets SqlInNode.Negated (negation arrives as SqlNotNode);
            // honor it anyway so both spellings agree.
            if (inNode.Negated)
            {
                EmitInNegated(sql, inNode, entityType, alias);
                return;
            }

            var (nonNulls, hasNull) = LargeListHelper.PartitionNulls(inNode.Values);
            var col = Quote(ModelBinder.GetColumnName(inNode.Property, entityType));

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

            if (!UseFastIn(inNode, nonNulls.Count))
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
                var json = LargeListHelper.BuildJsonArray(nonNulls);
                sql.Append(col).Append(" IN (SELECT \"i\".\"value\" FROM json_each(");
                EmitValue(sql, json);
                sql.Append(") AS \"i\")");
            }

            if (parenthesize)
            {
                sql.Append(" OR ").Append(col).Append(" IS NULL)");
            }
        }

        private void EmitInNegated(StringBuilder sql, SqlInNode inNode, IEntityType entityType, string? alias)
        {
            var (nonNulls, hasNull) = LargeListHelper.PartitionNulls(inNode.Values);
            var col = Quote(ModelBinder.GetColumnName(inNode.Property, entityType));

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

            if (!UseFastIn(inNode, nonNulls.Count))
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
                var json = LargeListHelper.BuildJsonArray(nonNulls);
                sql.Append(col).Append(" NOT IN (SELECT \"i\".\"value\" FROM json_each(");
                EmitValue(sql, json);
                sql.Append(") AS \"i\")");
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

        private void EmitIsEmpty(StringBuilder sql, SqlIsEmptyNode empty, IEntityType entityType, string? alias)
        {
            var col = Quote(ModelBinder.GetColumnName(empty.Property, entityType));

            if (empty.Negated)
            {
                sql.Append("NOT ");
            }

            sql.Append('(').Append(col).Append(" IS NULL OR ").Append(col).Append(" = '')");
        }
    }
}
