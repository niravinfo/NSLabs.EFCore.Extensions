using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Internal;

internal sealed class SqliteProvider : IBulkProvider
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.Sqlite";

    public IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand)
        => SqliteSqlGenerator.Generate(operations, maxParametersPerCommand);

    public Task<Dictionary<int, int>> ExecuteAsync(
        DbContext context,
        IReadOnlyList<SqlChunkPlan> chunks,
        IReadOnlyList<BoundOperation> operations,
        BulkExecuteOptions options,
        CancellationToken cancellationToken)
        => SqliteExecutor.ExecuteAsync(context, chunks, operations, options, cancellationToken);
}

internal static class SqliteProviderRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        BulkProviderRegistry.Register(new SqliteProvider());
    }
}
