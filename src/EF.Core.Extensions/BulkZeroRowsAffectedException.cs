namespace EF.Core.Extensions;

public sealed class BulkZeroRowsAffectedException(int operationIndex, string entityType)
    : InvalidOperationException($"Bulk operation #{operationIndex} targeting '{entityType}' affected 0 rows.")
{
    public int OperationIndex { get; } = operationIndex;

    public string EntityType { get; } = entityType;
}
