using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace NSLabs.EFCore.Extensions.Internal;

internal sealed class SqliteProvider : IBulkProvider
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.Sqlite";

    public IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand, DbContext context)
        => SqliteSqlGenerator.Generate(operations, maxParametersPerCommand);

    public Task<Dictionary<int, int>> ExecuteAsync(
        DbContext context,
        IReadOnlyList<SqlChunkPlan> chunks,
        IReadOnlyList<BoundOperation> operations,
        BulkExecuteOptions options,
        ILogger? logger,
        CancellationToken cancellationToken)
        => SqliteExecutor.ExecuteAsync(context, chunks, operations, options, logger, cancellationToken);
}

internal static class SqliteProviderRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        BulkProviderRegistry.Register(new SqliteProvider());
    }
}
