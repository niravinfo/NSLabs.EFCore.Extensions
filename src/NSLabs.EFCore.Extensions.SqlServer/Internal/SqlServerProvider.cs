using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.SqlServer.Infrastructure.Internal;

namespace NSLabs.EFCore.Extensions.Internal;

internal sealed class SqlServerProvider : IBulkProvider
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";

    public IReadOnlyList<SqlChunkPlan> Generate(IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand, DbContext context)
        => SqlServerSqlGenerator.Generate(operations, maxParametersPerCommand, ResolveCompatibilityLevel(context));

    private static int ResolveCompatibilityLevel(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Single source of truth: inherit EF Core's configured compat level (UseCompatibilityLevel)
        // instead of a library-owned knob that could drift from it. EF supplies its own default
        // per version — 150 on EF9/10, 160 on EF11 — including for the missing-config
        // fallback below, so this file hardcodes no level.
        // Never probe the server (DbConnection.ServerVersion, sys.databases): engine version
        // != per-database compat, and EF itself rejected probing (dotnet/efcore#32528).
        // EF1001 note: SqlServerOptionsExtension lives in an Infrastructure.Internal namespace and
        // may change in a future EF major. That is contained: only this provider package touches it,
        // and the EF major is pinned centrally (Directory.Packages.props), so such a rename can only
        // arrive with a major we consciously adopt.
#pragma warning disable EF1001
        try
        {
            var extension = context.GetService<IDbContextOptions>()
                ?.FindExtension<SqlServerOptionsExtension>();
            if (extension is null)
            {
                return SqlServerOptionsExtension.SqlServerDefaultCompatibilityLevel;
            }

            return extension.EngineType switch
            {
                SqlServerEngineType.AzureSql => extension.AzureSqlCompatibilityLevel,
                SqlServerEngineType.AzureSynapse => extension.AzureSynapseCompatibilityLevel,
                _ => extension.SqlServerCompatibilityLevel,
            };
        }
        catch (InvalidOperationException)
        {
            // No provider configured (or services not built). Unreachable in production —
            // BulkBatch resolves the provider by name first — but resolution must never
            // fail the batch on missing config.
            return SqlServerOptionsExtension.SqlServerDefaultCompatibilityLevel;
        }
#pragma warning restore EF1001
    }

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
