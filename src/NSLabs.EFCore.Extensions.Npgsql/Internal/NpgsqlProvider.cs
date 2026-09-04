using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Internal;

internal sealed class NpgsqlProvider : IBulkProvider
{
    public string ProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";

    public IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand)
        => NpgsqlSqlGenerator.Generate(operations, maxParametersPerCommand);

    public Task<Dictionary<int, int>> ExecuteAsync(
        DbContext context,
        IReadOnlyList<SqlChunkPlan> chunks,
        IReadOnlyList<BoundOperation> operations,
        BulkExecuteOptions options,
        CancellationToken cancellationToken)
        => NpgsqlExecutor.ExecuteAsync(context, chunks, operations, options, cancellationToken);
}

internal static class NpgsqlProviderRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        BulkProviderRegistry.Register(new NpgsqlProvider());
    }
}
