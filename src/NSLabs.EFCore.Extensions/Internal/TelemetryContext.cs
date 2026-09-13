using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NSLabs.EFCore.Extensions.Diagnostics;

namespace NSLabs.EFCore.Extensions.Internal;

/// <summary>
/// Pre-resolved per-batch telemetry context. Null means "not observed":
/// zero-cost path, downstream falls back to defaults.
/// </summary>
internal sealed record TelemetryContext(
    BulkInstrumentationOptions? Instrumentation,
    string? DbSystem,
    string? DbName)
{
    public static TelemetryContext? Resolve(DbContext context, DbConnection connection)
    {
        if (!BulkExecuteTelemetry.Source.HasListeners())
        {
            return null;
        }

        try
        {
            var instrumentation = BulkBatch.ResolveInstrumentationEffective();
            var dbSystem = BulkExecuteTelemetry.DbSystem(context.Database.ProviderName);
            var dbName = connection.Database;
            if (string.IsNullOrEmpty(dbName))
            {
                dbName = null;
            }

            return new TelemetryContext(instrumentation, dbSystem, dbName);
        }
        catch
        {
            return null;
        }
    }
}
