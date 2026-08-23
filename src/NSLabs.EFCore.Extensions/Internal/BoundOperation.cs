using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal enum BulkOperationKind
{
    Update,
    Delete,
    Upsert
}

internal sealed class BoundAssignment
{
    public required IProperty Property { get; init; }

    public required object? Value { get; init; }
}

internal sealed class BoundUpsertSpec
{
    public List<IProperty>? ConflictProperties { get; set; }

    public SqlNode? Guard { get; set; }

    public int RowCount { get; set; }
}

internal sealed class BoundOperation
{
    public required int GlobalIndex { get; init; }

    public required BulkOperationKind Kind { get; init; }

    public required IEntityType EntityType { get; init; }

    public List<BoundAssignment> Assignments { get; } = [];

    public List<SqlNode> PredicateParts { get; } = [];

    public BoundUpsertSpec? UpsertSpec { get; set; }
}
