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

internal sealed class BoundUpsertRow
{
    public required IReadOnlyList<BoundAssignment> InsertValues { get; init; }

    public required IReadOnlyList<object?> KeyValues { get; init; }
}

internal sealed class BoundUpsertSpec
{
    public required List<IProperty> ConflictProperties { get; init; }

    /// <summary>All columns carried in the MERGE source, in emission order.</summary>
    public required List<IProperty> InsertColumns { get; init; }

    /// <summary>Columns written on match when no explicit Set(...) payload exists.</summary>
    public required List<IProperty> UpdateColumns { get; init; }

    public SqlNode? Guard { get; set; }

    public List<BoundUpsertRow> Rows { get; } = [];
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
