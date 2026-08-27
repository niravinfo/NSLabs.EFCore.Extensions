using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Internal;

internal sealed class SqlServerProvider : IBulkProvider
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";

    public IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand)
        => SqlServerSqlGenerator.Generate(operations, maxParametersPerCommand);

    public Task<Dictionary<int, int>> ExecuteAsync(
        DbContext context,
        IReadOnlyList<SqlChunkPlan> chunks,
        IReadOnlyList<BoundOperation> operations,
        BulkExecuteOptions options,
        CancellationToken cancellationToken)
        => SqlServerExecutor.ExecuteAsync(context, chunks, operations, options, cancellationToken);
}

internal static class SqlServerProviderRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        BulkProviderRegistry.Register(new SqlServerProvider());
    }
}
