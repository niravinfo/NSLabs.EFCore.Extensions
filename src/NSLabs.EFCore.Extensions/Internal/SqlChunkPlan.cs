namespace NSLabs.EFCore.Extensions.Internal;

internal sealed class SqlChunkPlan
{
    public required string CommandText { get; init; }

    public required IReadOnlyList<SqlParam> Parameters { get; init; }

    public required IReadOnlyList<int> OperationIndices { get; init; }
}
