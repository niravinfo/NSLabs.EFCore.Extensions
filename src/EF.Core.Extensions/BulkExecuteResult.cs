namespace EF.Core.Extensions;

public sealed record OperationResult(string EntityType, int RowsAffected);

public sealed record BulkExecuteResult
{
    public required int TotalRowsAffected { get; init; }

    public required IReadOnlyList<OperationResult> Operations { get; init; }

    public static BulkExecuteResult Empty { get; } =
        new() { TotalRowsAffected = 0, Operations = [] };
}
