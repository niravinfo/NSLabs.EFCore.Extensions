using System.Text;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class SqliteSqlGenerator
{
    internal const int MaxParametersPerCommand = 999;

    // Large-IN mark (LARGE_LIST_SUPPORT_PLAN.md §3): lists above this many non-null
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
            sql.Append(" WHERE ").Append(emitter.EmitGuard(guard, entityType));
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

        public string EmitGuard(SqlNode node, IEntityType entityType)
        {
            _inGuard = true;
            try
            {
                return Emit(node, entityType);
            }
            finally
            {
                _inGuard = false;
            }
        }

        public string Emit(SqlNode node, IEntityType entityType, string? alias = null) => node switch
        {
            SqlColumnNode column => Quote(ModelBinder.GetColumnName(column.Property, entityType)),
            SqlBooleanNode boolean => $"{Quote(ModelBinder.GetColumnName(boolean.Property, entityType))} = 1",
            SqlParameterNode parameter => EmitValue(parameter.Value),
            SqlNullCheckNode nullCheck => $"{Quote(ModelBinder.GetColumnName(nullCheck.Property, entityType))} {(nullCheck.IsNotNull ? "IS NOT NULL" : "IS NULL")}",
            SqlNotNode not => EmitNot(not, entityType, alias),
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
                case "LEAST" when method.Args.Count == 2: return $"MIN({Emit(method.Args[0], entityType, alias)}, {Emit(method.Args[1], entityType, alias)})";
                case "GREATEST" when method.Args.Count == 2: return $"MAX({Emit(method.Args[0], entityType, alias)}, {Emit(method.Args[1], entityType, alias)})";
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

        // Negation of a bare Contains distributes per EF10 (§4.1) instead of wrapping
        // in NOT (...): the wrapper is wrong for nullable columns in both directions.
        // Only a NOT whose direct inner is an IN distributes; deeper NOTs keep the wrapper.
        private string EmitNot(SqlNotNode not, IEntityType entityType, string? alias)
        {
            if (not.Inner is SqlInNode inNode)
            {
                return EmitInNegated(inNode, entityType, alias);
            }

            return $"NOT ({Emit(not.Inner, entityType, alias)})";
        }

        private bool UseFastIn(SqlInNode inNode, int nonNullCount)
        {
            var flippable = !LargeListHelper.IsByteArrayElementList(inNode.Values);
            var opOverBudget = _inGuard ? _guardOverBudget : _opOverBudget;
            return LargeListHelper.UseFastInPath(nonNullCount, flippable, LargeListThreshold, opOverBudget);
        }

        private string EmitIn(SqlInNode inNode, IEntityType entityType, string? alias)
        {
            // The translator never sets SqlInNode.Negated (negation arrives as SqlNotNode);
            // honor it anyway so both spellings agree.
            if (inNode.Negated)
            {
                return EmitInNegated(inNode, entityType, alias);
            }

            var (nonNulls, hasNull) = LargeListHelper.PartitionNulls(inNode.Values);
            var col = Quote(ModelBinder.GetColumnName(inNode.Property, entityType));

            if (nonNulls.Count == 0)
            {
                return hasNull ? $"{col} IS NULL" : "1=0";
            }

            // OR-expansions are parenthesized: predicate parts are AND-joined bare,
            // so a bare "... OR ... IS NULL" would misbind to its neighbors.
            var nullBranch = hasNull && inNode.Property.IsNullable ? $" OR {col} IS NULL" : "";

            if (!UseFastIn(inNode, nonNulls.Count))
            {
                var sb = StringBuilderCache.Acquire(32);
                try
                {
                    sb.Append(col).Append(" IN (");
                    for (var i = 0; i < nonNulls.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(EmitValue(nonNulls[i]));
                    }
                    sb.Append(')');
                    var slow = StringBuilderCache.GetStringAndRelease(sb);
                    return nullBranch.Length == 0 ? slow : $"({slow}{nullBranch})";
                }
                catch
                {
                    StringBuilderCache.Release(sb);
                    throw;
                }
            }

            var json = LargeListHelper.BuildJsonArray(nonNulls);
            var param = EmitValue(json);
            var fast = $"{col} IN (SELECT \"i\".\"value\" FROM json_each({param}) AS \"i\")";
            return nullBranch.Length == 0 ? fast : $"({fast}{nullBranch})";
        }

        private string EmitInNegated(SqlInNode inNode, IEntityType entityType, string? alias)
        {
            var (nonNulls, hasNull) = LargeListHelper.PartitionNulls(inNode.Values);
            var col = Quote(ModelBinder.GetColumnName(inNode.Property, entityType));

            if (nonNulls.Count == 0)
            {
                return hasNull ? $"{col} IS NOT NULL" : "1=1";
            }

            string core;
            if (!UseFastIn(inNode, nonNulls.Count))
            {
                var sb = StringBuilderCache.Acquire(32);
                try
                {
                    sb.Append(col).Append(" NOT IN (");
                    for (var i = 0; i < nonNulls.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(EmitValue(nonNulls[i]));
                    }
                    sb.Append(')');
                    core = StringBuilderCache.GetStringAndRelease(sb);
                }
                catch
                {
                    StringBuilderCache.Release(sb);
                    throw;
                }
            }
            else
            {
                var json = LargeListHelper.BuildJsonArray(nonNulls);
                var param = EmitValue(json);
                core = $"{col} NOT IN (SELECT \"i\".\"value\" FROM json_each({param}) AS \"i\")";
            }

            if (hasNull && inNode.Property.IsNullable)
            {
                return $"{core} AND {col} IS NOT NULL";
            }

            // OR-expansion: parenthesize (see EmitIn).
            return inNode.Property.IsNullable ? $"({core} OR {col} IS NULL)" : core;
        }

        private string EmitIsEmpty(SqlIsEmptyNode empty, IEntityType entityType, string? alias)
        {
            var col = Quote(ModelBinder.GetColumnName(empty.Property, entityType));
            var check = $"({col} IS NULL OR {col} = '')";
            return empty.Negated ? $"NOT {check}" : check;
        }
    }
}
