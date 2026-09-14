using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace NSLabs.EFCore.Extensions.Internal;

internal sealed class NpgsqlProvider : IBulkProvider
{
    public string ProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";

    public IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand, DbContext context)
        => NpgsqlSqlGenerator.Generate(operations, maxParametersPerCommand);

    public Task<Dictionary<int, int>> ExecuteAsync(
        DbContext context,
        IReadOnlyList<SqlChunkPlan> chunks,
        IReadOnlyList<BoundOperation> operations,
        BulkExecuteOptions options,
        ILogger? logger,
        CancellationToken cancellationToken)
        => NpgsqlExecutor.ExecuteAsync(context, chunks, operations, options, logger, cancellationToken);
}

internal static class NpgsqlProviderRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        BulkProviderRegistry.Register(new NpgsqlProvider());
    }
}
