using System.Text;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class SqlServerSqlGenerator
{
    private const string TargetAlias = "t";

    private const string SourceAlias = "s";

    // Large-IN mark (docs/DESIGN.md "Large IN lists"): lists above this many non-null
    // values use the single-param OPENJSON path. Internal const in v1 — no public option.
    internal const int LargeListThreshold = 100;

    public static IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand, int sqlServerCompatibilityLevel = 150)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParametersPerCommand, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sqlServerCompatibilityLevel, 1);

        // SAFETY S8: presizing never changes chunk boundaries; only reduces reallocations
        var chunks = new List<SqlChunkPlan>(Math.Min(operations.Count, 16));
        var pending = new List<PendingUnit>(Math.Min(operations.Count, 16));
        var pendingParamCount = 0;

        foreach (var operation in operations)
        {
            if (operation.Kind == BulkOperationKind.Upsert)
            {
                ExpandUpsert(operation, pending, chunks, maxParametersPerCommand, sqlServerCompatibilityLevel, ref pendingParamCount);
                continue;
            }

            // Fast-aware cost: large IN lists collapse to 1 param before the budget check.
            var opOverBudget = LargeListHelper.SlowOpTotal(operation) > maxParametersPerCommand;
            var cost = CountDecidedTotal(operation, opOverBudget);

            if (cost > maxParametersPerCommand)
            {
                throw new InvalidOperationException(
                    $"Operation #{operation.GlobalIndex} requires {cost} parameters which exceeds MaxParametersPerCommand={maxParametersPerCommand}. Increase the limit or split the operation.");
            }

            if (pendingParamCount + cost > maxParametersPerCommand && pending.Count > 0)
            {
                chunks.Add(BuildChunk(pending, sqlServerCompatibilityLevel));
                pending = [];
                pendingParamCount = 0;
            }

            pending.Add(new PendingUnit(operation, 0, 0, opOverBudget, GuardOverBudget: false));
            pendingParamCount += cost;
        }

        if (pending.Count > 0)
        {
            chunks.Add(BuildChunk(pending, sqlServerCompatibilityLevel));
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
        int sqlServerCompatibilityLevel,
        ref int pendingParamCount)
    {
        if (operation.UpsertSpec is not { } spec)
        {
            throw new InvalidOperationException($"Upsert operation #{operation.GlobalIndex} was not bound to an upsert spec.");
        }

        if (spec.Rows.Count == 0)
        {
            pending.Add(new PendingUnit(operation, 0, 0, OpOverBudget: false, GuardOverBudget: false));
            return;
        }

        var perRowCost = spec.InsertColumns.Count;

        // Fast-aware guard cost (§4.1): the guard's IN nodes decide against the
        // worst-case single-row unit total, so a large-IN guard collapses to 1 param here.
        var guardOverBudget =
            LargeListHelper.SlowGuardUnitTotal(spec.Guard, operation.Assignments, perRowCost) > maxParametersPerCommand;

        // EF Core pattern: manual loop vs LINQ Sum — avoids enumerator alloc in hot ExpandUpsert
        var fixedCost = CountDecidedNode(spec.Guard, guardOverBudget);
        for (var i = 0; i < operation.Assignments.Count; i++)
        {
            var assignment = operation.Assignments[i];
            fixedCost += assignment.ValueExpression is not null ? CountDecidedNode(assignment.ValueExpression, guardOverBudget) : 1;
        }

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
                FlushPending(pending, chunks, sqlServerCompatibilityLevel, ref pendingParamCount);
                continue;
            }

            var rowCount = Math.Min(spec.Rows.Count - startRow, capacity);
            pending.Add(new PendingUnit(operation, startRow, rowCount, guardOverBudget, guardOverBudget));
            pendingParamCount += fixedCost + rowCount * perRowCost;
            startRow += rowCount;

            // The remaining rows cannot join this chunk (another row never fits after a full
            // fill), so flush now to keep chunk boundaries clean for subsequent operations.
            if (startRow < spec.Rows.Count)
            {
                FlushPending(pending, chunks, sqlServerCompatibilityLevel, ref pendingParamCount);
            }
        }
    }

    private static void FlushPending(List<PendingUnit> pending, List<SqlChunkPlan> chunks, int sqlServerCompatibilityLevel, ref int pendingParamCount)
    {
        if (pending.Count == 0)
        {
            return;
        }

        chunks.Add(BuildChunk(pending, sqlServerCompatibilityLevel));
        pending.Clear();
        pendingParamCount = 0;
    }

    private static SqlChunkPlan BuildChunk(IReadOnlyList<PendingUnit> units, int sqlServerCompatibilityLevel)
    {
        // SAFETY S2,S11: distinct indices computed once, order preserved (insertion order); SQL is identical
        var distinctIndices = GetDistinctIndices(units);
        var estimatedParamCount = 0;
        foreach (var u in units)
        {
            // Rough estimate for presizing; exact count not required for correctness
            estimatedParamCount += u.Operation.Kind == BulkOperationKind.Upsert ? u.RowCount * 3 : 3;
        }

        var emitter = new ParameterEmitter(Math.Max(estimatedParamCount, 4), sqlServerCompatibilityLevel);
        // EF Core pattern: StringBuilderCache (ThreadStatic pooling, max 1024) — reduces Gen0 per BuildChunk in hot loops
        var sql = StringBuilderCache.Acquire(256 + (units.Count * 180) + (distinctIndices.Count * 32));

        try
        {
            foreach (var index in distinctIndices)
            {
                sql.Append("DECLARE @rc").Append(index).AppendLine(" int;");
            }

            foreach (var unit in units)
            {
                emitter.BeginOperation(unit.OpOverBudget, unit.GuardOverBudget);

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

            var plan = new SqlChunkPlan
            {
                CommandText = StringBuilderCache.GetStringAndRelease(sql),
                Parameters = emitter.Parameters,
                OperationIndices = distinctIndices.ToArray()
            };
            return plan;
        }
        catch
        {
            // Release the pooled builder on the failure path too so a later chunk can reuse it.
            StringBuilderCache.Release(sql);
            throw;
        }
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
        sql.Append(';').AppendLine();
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

                emitter.EmitValue(sql, row.InsertValues[c].Value);
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
                sql.Append(" AND ");
                emitter.EmitGuard(sql, guard, entityType, TargetAlias);
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
                        .Append(" = ");

                    if (assignment.ValueExpression is not null)
                    {
                        emitter.Emit(sql, assignment.ValueExpression, entityType, TargetAlias);
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

    private static void EmitPredicate(ParameterEmitter emitter, StringBuilder sql, BoundOperation operation)
    {
        if (operation.PredicateParts.Count == 0)
        {
            throw new InvalidOperationException(
                $"Operation #{operation.GlobalIndex} on '{operation.EntityType.DisplayName()}' has no predicate; refusing to emit unbounded DML.");
        }

        if (operation.PredicateParts.Count == 1)
        {
            emitter.Emit(sql, operation.PredicateParts[0], operation.EntityType);
            return;
        }

        // EF Core pattern: manual loop vs string.Join + Select — avoids LINQ alloc per UPDATE/DELETE
        for (var i = 0; i < operation.PredicateParts.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(" AND ");
            }

            emitter.Emit(sql, operation.PredicateParts[i], operation.EntityType);
        }
    }

    // Fast-aware total: IN nodes cost 1 on the fast path, nonNullCount otherwise
    // (shared LargeListHelper rule — same pure rule the emitter uses, so plans and params agree).
    private static int CountDecidedTotal(BoundOperation operation, bool opOverBudget)
    {
        // EF Core pattern: manual loop vs LINQ Sum
        var count = 0;
        for (var i = 0; i < operation.Assignments.Count; i++)
        {
            var assignment = operation.Assignments[i];
            count += assignment.ValueExpression is not null ? CountDecidedNode(assignment.ValueExpression, opOverBudget) : 1;
        }

        foreach (var part in operation.PredicateParts)
        {
            count += CountDecidedNode(part, opOverBudget);
        }

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
        // EF Core pattern: manual loop vs LINQ Sum
        var sum = 0;
        for (var i = 0; i < method.Args.Count; i++)
        {
            sum += CountDecidedNode(method.Args[i], opOverBudget);
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

    // OpOverBudget/GuardOverBudget ride along so the emitter reuses the exact decisions
    // counting made (pure functions of the same inputs — agreement by construction).
    // For upsert units both flags carry the guard-unit decision; assignments never
    // contain IN nodes, so either flag would agree for them.
    private readonly record struct PendingUnit(
        BoundOperation Operation,
        int StartRow,
        int RowCount,
        bool OpOverBudget,
        bool GuardOverBudget);

    private sealed class ParameterEmitter
    {
        private readonly List<SqlParam> _parameters;

        private readonly int _compatibilityLevel;

        public IReadOnlyList<SqlParam> Parameters => _parameters;

        private int Counter { get; set; }

        // Per-operation IN decision flags (set by BeginOperation per emitted op).
        // _inGuard selects the guard-unit flag while a MERGE guard is emitting.
        private bool _opOverBudget;

        private bool _guardOverBudget;

        private bool _inGuard;

        public ParameterEmitter(int capacity = 8, int compatibilityLevel = 150)
        {
            _parameters = new List<SqlParam>(capacity);
            _compatibilityLevel = compatibilityLevel;
        }

        public void BeginOperation(bool opOverBudget, bool guardOverBudget)
        {
            _opOverBudget = opOverBudget;
            _guardOverBudget = guardOverBudget;
            _inGuard = false;
        }

        public void EmitGuard(StringBuilder sql, SqlNode node, IEntityType entityType, string? alias)
        {
            _inGuard = true;
            try
            {
                Emit(sql, node, entityType, alias);
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
                    AppendAlias(sql, alias);
                    sql.Append(Quote(ModelBinder.GetColumnName(column.Property, entityType)));
                    break;

                case SqlBooleanNode boolean:
                    AppendAlias(sql, alias);
                    sql.Append(Quote(ModelBinder.GetColumnName(boolean.Property, entityType))).Append(" = 1");
                    break;

                case SqlParameterNode parameter:
                    EmitValue(sql, parameter.Value);
                    break;

                case SqlNullCheckNode nullCheck:
                    AppendAlias(sql, alias);
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
                    sql.Append('(');
                    Emit(sql, arithmetic.Left, entityType, alias);
                    sql.Append(' ').Append(RenderArithmetic(arithmetic.Operator)).Append(' ');
                    Emit(sql, arithmetic.Right, entityType, alias);
                    sql.Append(')');
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

        private static void AppendAlias(StringBuilder sql, string? alias)
        {
            if (alias is not null)
            {
                sql.Append('[').Append(alias).Append("].");
            }
        }

        private static string WithAlias(string? alias) => alias is null ? "" : $"[{alias}].";

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
                if (i > 0)
                {
                    sql.Append(", ");
                }

                Emit(sql, method.Args[i], entityType, alias);
            }
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

        private void EmitMethod(StringBuilder sql, SqlMethodCallNode method, IEntityType entityType, string? alias)
        {
            // EF Core pattern: switch with manual string building — avoids LINQ in CONCAT
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
                    sql.Append("LTRIM(RTRIM(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append("))");
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
                    sql.Append("LEN(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "SUBSTRING":
                    sql.Append("SUBSTRING(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(", ");
                    EmitArg(sql, method, 1, entityType, alias);
                    sql.Append(", ");
                    EmitArg(sql, method, 2, entityType, alias);
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
                    // Manual loop vs string.Join + Select — preserves Emit side-effects order
                    sql.Append("CONCAT(");
                    EmitArgs(sql, method, entityType, alias);
                    sql.Append(')');
                    break;

                case "ABS":
                    sql.Append("ABS(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "CEILING":
                    sql.Append("CEILING(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "FLOOR":
                    sql.Append("FLOOR(");
                    EmitArg(sql, method, 0, entityType, alias);
                    sql.Append(')');
                    break;

                case "ROUND" when method.Args.Count == 2:
                case "ROUND" when method.Args.Count == 3:
                    sql.Append("ROUND(");
                    EmitArgs(sql, method, entityType, alias);
                    sql.Append(')');
                    break;

                case "LEAST" when method.Args.Count == 2:
                case "GREATEST" when method.Args.Count == 2:
                    EmitLeastGreatest(sql, method, entityType, alias);
                    break;

                default:
                    throw new NotSupportedException($"Method '{method.Method}' is not supported for SQL generation.");
            }
        }

        private void EmitLeastGreatest(StringBuilder sql, SqlMethodCallNode method, IEntityType entityType, string? alias)
        {
            // LEAST/GREATEST require SQL Server 2022 (compatibility level 160+), mirroring EF Core.
            // No CASE WHEN fallback: LEAST ignores NULLs while CASE WHEN takes the ELSE branch —
            // fail fast instead of silently changing semantics.
            if (_compatibilityLevel < 160)
            {
                throw new NotSupportedException(
                    $"Method '{method.Method}' requires SQL Server compatibility level 160 (SQL Server 2022) or higher, but the configured level is {_compatibilityLevel}. " +
                    $"Configure it via UseSqlServer(..., b => b.UseCompatibilityLevel(160)) and ensure the database allows it (ALTER DATABASE <name> SET COMPATIBILITY_LEVEL = 160).");
            }

            sql.Append(method.Method).Append('(');
            EmitArgs(sql, method, entityType, alias);
            sql.Append(')');
        }

        private void EmitLike(StringBuilder sql, SqlLikeNode like, IEntityType entityType, string? alias)
        {
            AppendAlias(sql, alias);
            sql.Append(Quote(ModelBinder.GetColumnName(like.Property, entityType)));

            var raw = like.PatternValue as string ?? throw new NotSupportedException("LIKE pattern must be a string.");
            var finalPattern = BuildLikePattern(raw, like.Kind);
            sql.Append(like.Negated ? " NOT LIKE " : " LIKE ");
            EmitValue(sql, finalPattern);
        }

        private static string BuildLikePattern(string raw, SqlLikeKind kind) => kind switch
        {
            SqlLikeKind.Contains => $"%{EscapeLikeServer(raw)}%",
            SqlLikeKind.StartsWith => $"{EscapeLikeServer(raw)}%",
            SqlLikeKind.EndsWith => $"%{EscapeLikeServer(raw)}",
            SqlLikeKind.Like => raw,
            _ => throw new NotSupportedException($"SqlLikeKind '{kind}' is not supported.")
        };

        private static string EscapeLikeServer(string pattern)
        {
            if (pattern.IndexOf('[') < 0 && pattern.IndexOf('%') < 0 && pattern.IndexOf('_') < 0)
            {
                return pattern;
            }

            var sb = StringBuilderCache.Acquire(pattern.Length + 8);
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '[') sb.Append("[[]");
                else if (c == '%') sb.Append("[%]");
                else if (c == '_') sb.Append("[_]");
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
            var col = $"{WithAlias(alias)}{Quote(ModelBinder.GetColumnName(inNode.Property, entityType))}";

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
                // EF Core pattern: manual loop vs string.Join+Select — avoids enumerator alloc for IN lists
                sql.Append(col).Append(" IN (");
                for (var i = 0; i < nonNulls.Count; i++)
                {
                    if (i > 0)
                    {
                        sql.Append(", ");
                    }

                    EmitValue(sql, nonNulls[i]);
                }

                sql.Append(')');
            }
            else
            {
                EmitOpenJson(sql, col, inNode.Property, nonNulls);
            }

            if (parenthesize)
            {
                sql.Append(" OR ").Append(col).Append(" IS NULL)");
            }
        }

        // Single-param OPENJSON predicate. Typed WITH when the converted domain is
        // exactly known; untyped default-schema fallback (`[v].[value]`) otherwise —
        // untyped is always result-identical via implicit conversion, only less seekable.
        private void EmitOpenJson(StringBuilder sql, string col, IProperty property, List<object?> nonNulls)
        {
            var json = LargeListHelper.BuildJsonArray(nonNulls);
            var withType = SqlServerLargeList.OpenJsonWithType(property, nonNulls);

            sql.Append(col)
                .Append(" IN (SELECT ")
                .Append(withType is null ? "[v].[value]" : "[v].[Value]")
                .Append(" FROM OPENJSON(");
            EmitValue(sql, json);

            if (withType is null)
            {
                sql.Append(") AS [v])");
            }
            else
            {
                sql.Append(") WITH ([Value] ").Append(withType).Append(" '$') AS [v])");
            }
        }

        private void EmitOpenJsonNegated(StringBuilder sql, string col, IProperty property, List<object?> nonNulls)
        {
            var json = LargeListHelper.BuildJsonArray(nonNulls);
            var withType = SqlServerLargeList.OpenJsonWithType(property, nonNulls);

            sql.Append(col)
                .Append(" NOT IN (SELECT ")
                .Append(withType is null ? "[v].[value]" : "[v].[Value]")
                .Append(" FROM OPENJSON(");
            EmitValue(sql, json);

            if (withType is null)
            {
                sql.Append(") AS [v])");
            }
            else
            {
                sql.Append(") WITH ([Value] ").Append(withType).Append(" '$') AS [v])");
            }
        }

        private void EmitInNegated(StringBuilder sql, SqlInNode inNode, IEntityType entityType, string? alias)
        {
            var (nonNulls, hasNull) = LargeListHelper.PartitionNulls(inNode.Values);
            var col = $"{WithAlias(alias)}{Quote(ModelBinder.GetColumnName(inNode.Property, entityType))}";

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
                    if (i > 0)
                    {
                        sql.Append(", ");
                    }

                    EmitValue(sql, nonNulls[i]);
                }

                sql.Append(')');
            }
            else
            {
                EmitOpenJsonNegated(sql, col, inNode.Property, nonNulls);
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
            var col = $"{WithAlias(alias)}{Quote(ModelBinder.GetColumnName(empty.Property, entityType))}";

            if (empty.Negated)
            {
                sql.Append("NOT ");
            }

            sql.Append('(').Append(col).Append(" IS NULL OR ").Append(col).Append(" = '')");
        }
    }
}
