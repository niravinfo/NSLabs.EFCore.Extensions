using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Internal;

internal interface IBulkProvider
{
    string ProviderName { get; }

    IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand);

    Task<Dictionary<int, int>> ExecuteAsync(
        DbContext context,
        IReadOnlyList<SqlChunkPlan> chunks,
        IReadOnlyList<BoundOperation> operations,
        BulkExecuteOptions options,
        CancellationToken cancellationToken);
}
