using System.Text.Json;

namespace NSLabs.EFCore.Extensions.Internal;

// Shared, provider-neutral building blocks for large `IN`-list support
// (docs/DESIGN.md "Large IN lists"). This file must never contain provider
// dialect — no OPENJSON/ANY/json_each text, no WITH types, no per-provider
// thresholds. Each provider owns its dialect next to its generator and calls
// into these pure functions. Counting and emission use identical inputs, so
// chunk plans and emitted params agree by construction.
internal static class LargeListHelper
{
    // Number of list elements that occupy a parameter. Nulls are stripped before
    // counting (§4.1) — they cost 0 params on either path.
    public static int NonNullCount(IReadOnlyList<object?> values)
    {
        var count = 0;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not null)
            {
                count++;
            }
        }

        return count;
    }

    public static (List<object?> NonNulls, bool HasNull) PartitionNulls(IReadOnlyList<object?> values)
    {
        var nonNulls = new List<object?>(values.Count);
        var hasNull = false;
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (value is null)
            {
                hasNull = true;
            }
            else
            {
                nonNulls.Add(value);
            }
        }

        return (nonNulls, hasNull);
    }

    // v1: byte[]-element lists (List<byte[]> vs varbinary) stay on the multi-param
    // route — System.Text.Json would base64 them, which does not round-trip through
    // a typed single-param shape. Element type follows the first-non-null rule.
    public static bool IsByteArrayElementList(IReadOnlyList<object?> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not null)
            {
                return values[i] is byte[];
            }
        }

        return false;
    }

    // Pure per-IN decision (§4.1): threshold-first mirrors EF's per-collection switch;
    // opOverBudget fixes EF's combined-overflow bug (dotnet/efcore#37347) instead of copying it.
    public static bool UseFastInPath(int nonNullCount, bool flippable, int threshold, bool opOverBudget)
    {
        if (nonNullCount == 0 || !flippable)
        {
            return false;
        }

        return nonNullCount > threshold || opOverBudget;
    }

    public static int DecidedInCost(SqlInNode node, int threshold, bool opOverBudget)
    {
        var nonNullCount = NonNullCount(node.Values);
        var flippable = !IsByteArrayElementList(node.Values);
        return UseFastInPath(nonNullCount, flippable, threshold, opOverBudget) ? 1 : nonNullCount;
    }

    // Slow-total walk: every IN at its slow (non-null) cost. Must stay arm-for-arm in
    // sync with each generator's decided-walk; only the SqlInNode arm differs.
    public static int SlowCountNode(SqlNode? node) => node switch
    {
        null => 0,
        SqlParameterNode => 1,
        SqlBinaryNode binary => SlowCountNode(binary.Left) + SlowCountNode(binary.Right),
        SqlNotNode not => SlowCountNode(not.Inner),
        SqlUnaryNode unary => SlowCountNode(unary.Inner),
        SqlConditionalNode cond => SlowCountNode(cond.Test) + SlowCountNode(cond.IfTrue) + SlowCountNode(cond.IfFalse),
        SqlCoalesceNode co => SlowCountNode(co.Left) + SlowCountNode(co.Right),
        SqlMethodCallNode method => SlowCountMethodArgs(method),
        SqlColumnNode => 0,
        SqlBooleanNode => 0,
        SqlNullCheckNode => 0,
        SqlLikeNode => 1,
        SqlInNode inNode => NonNullCount(inNode.Values),
        SqlIsEmptyNode => 0,
        _ => 0
    };

    public static int SlowCountMethodArgs(SqlMethodCallNode method)
    {
        var sum = 0;
        for (var i = 0; i < method.Args.Count; i++)
        {
            sum += SlowCountNode(method.Args[i]);
        }

        return sum;
    }

    public static int SlowAssignmentCost(BoundAssignment assignment)
        => assignment.ValueExpression is not null ? SlowCountNode(assignment.ValueExpression) : 1;

    // Op total with every IN expanded slow. Counter and emitter both derive
    // opOverBudget from this; identical inputs give identical decisions.
    public static int SlowOpTotal(BoundOperation operation)
    {
        var total = 0;
        for (var i = 0; i < operation.Assignments.Count; i++)
        {
            total += SlowAssignmentCost(operation.Assignments[i]);
        }

        foreach (var part in operation.PredicateParts)
        {
            total += SlowCountNode(part);
        }

        return total;
    }

    // Upsert-guard unit total: guard slow cost + matched-update assignment costs +
    // a single row's insert cost (worst-case single-row unit per §4.1).
    public static int SlowGuardUnitTotal(SqlNode? guard, IReadOnlyList<BoundAssignment> assignments, int perRowCost)
    {
        var total = SlowCountNode(guard) + perRowCost;
        for (var i = 0; i < assignments.Count; i++)
        {
            total += SlowAssignmentCost(assignments[i]);
        }

        return total;
    }

    // JSON payload over provider-converted, non-null values. System.Text.Json —
    // never hand-rolled: culture-invariant numerics, correct string escaping,
    // ISO-8601 dates (§4.2). Null never reaches the serializer (stripped per §4.1).
    public static string BuildJsonArray(IReadOnlyList<object?> nonNulls)
        => JsonSerializer.Serialize(nonNulls, JsonSerializerOptions.Default);
}
