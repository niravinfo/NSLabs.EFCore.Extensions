using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Diagnostics;

/// <summary>
/// Single home for bulk-execution tracing. Inbox-only (<see cref="ActivitySource"/>);
/// never references <c>OpenTelemetry.*</c>. Every entry point is null-tolerant and
/// never throws: telemetry must never break execution.
/// </summary>
internal static class BulkExecuteTelemetry
{
    internal static readonly ActivitySource Source = new(BulkExecuteTelemetryNames.SourceName, GetVersion());

    // Fallback when callers pass null telemetry while a listener is active.
    // Read-only: never mutate or expose — shared across all batches/chunks.
    private static readonly BulkInstrumentationOptions s_defaultInstrumentation = new();

    private static string GetVersion()
    {
        try
        {
            return typeof(BulkExecuteTelemetry).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }

    internal static string? DbSystem(string? providerName) => providerName switch
    {
        "Microsoft.EntityFrameworkCore.SqlServer" => "mssql",
        "Microsoft.EntityFrameworkCore.Sqlite" => "sqlite",
        "Npgsql.EntityFrameworkCore.PostgreSQL" => "postgresql",
        null => null,
        _ => ShortName(providerName),
    };

    private static string ShortName(string providerName)
    {
        var i = providerName.LastIndexOf('.');
        return i >= 0 && i < providerName.Length - 1 ? providerName[(i + 1)..] : providerName;
    }

    internal static string Truncate(string value, int maxLength, out bool truncated)
    {
        if (value.Length > maxLength)
        {
            truncated = true;
            return value.Substring(0, maxLength);
        }

        truncated = false;
        return value;
    }

    internal static BatchScope? StartBatchScope(
        DbContext context,
        string? providerName,
        IReadOnlyList<BoundOperation> operations,
        BulkExecuteOptions execution,
        BulkInstrumentationOptions? instrumentation,
        IReadOnlyList<SqlChunkPlan> chunks)
    {
        if (!Source.HasListeners())
        {
            return null;
        }

        try
        {
            var activity = Source.StartActivity("BulkExecute", ActivityKind.Client);
            if (activity is null)
            {
                return null;
            }

            var instr = instrumentation ?? s_defaultInstrumentation;

            var dbSystem = DbSystem(providerName);
            if (dbSystem is not null)
            {
                activity.SetTag("db.system", dbSystem);
            }

            try
            {
                var dbName = context.Database.GetDbConnection().Database;
                if (!string.IsNullOrEmpty(dbName))
                {
                    activity.SetTag("db.name", dbName);
                }
            }
            catch
            {
                // Database name is best-effort; never fail the batch for it.
            }

            activity.SetTag("db.operation", "bulk_execute");
            activity.SetTag("nslabs.bulk.provider", providerName);
            activity.SetTag("nslabs.bulk.operation_count", operations.Count);
            activity.SetTag("nslabs.bulk.chunk_count", chunks.Count);
            activity.SetTag("nslabs.bulk.throw_if_zero_affected", execution.ThrowIfZeroAffected);
            activity.SetTag("nslabs.bulk.max_parameters", execution.MaxParametersPerCommand);
            if (execution.CommandTimeout is { } timeout)
            {
                activity.SetTag("nslabs.bulk.command_timeout_s", timeout);
            }

            // EF Core pattern: manual loop vs LINQ Count+Where
            var updates = 0;
            var upserts = 0;
            var deletes = 0;
            for (var i = 0; i < operations.Count; i++)
            {
                switch (operations[i].Kind)
                {
                    case BulkOperationKind.Update: updates++; break;
                    case BulkOperationKind.Upsert: upserts++; break;
                    case BulkOperationKind.Delete: deletes++; break;
                }
            }

            activity.SetTag("nslabs.bulk.update_count", updates);
            activity.SetTag("nslabs.bulk.upsert_count", upserts);
            activity.SetTag("nslabs.bulk.delete_count", deletes);

            if (instr.CaptureCommandText && chunks.Count > 0)
            {
                AttachCommandText(activity, chunks[0].CommandText, instr.MaxCommandLength);
            }

            return new BatchScope(activity, instr.RecordException);
        }
        catch
        {
            return null;
        }
    }

    internal static ChunkScope? StartChunkScope(
        SqlChunkPlan chunk,
        int chunkIndex,
        TelemetryContext? telemetry)
    {
        // Check-then-allocate: no listener means zero overhead — return before
        // touching the fallback options when the caller passed null.
        if (!Source.HasListeners())
        {
            return null;
        }

        var instr = telemetry?.Instrumentation ?? s_defaultInstrumentation;
        if (!instr.EnableChunkSpans)
        {
            return null;
        }

        try
        {
            var activity = Source.StartActivity("BulkExecute.Chunk", ActivityKind.Client);
            if (activity is null)
            {
                return null;
            }

            if (telemetry?.DbSystem is not null)
            {
                activity.SetTag("db.system", telemetry.DbSystem);
            }

            if (!string.IsNullOrEmpty(telemetry?.DbName))
            {
                activity.SetTag("db.name", telemetry.DbName);
            }

            activity.SetTag("db.operation", "bulk_execute");
            activity.SetTag("nslabs.bulk.chunk.index", chunkIndex);
            activity.SetTag("nslabs.bulk.chunk.parameter_count", chunk.Parameters.Count);
            activity.SetTag("nslabs.bulk.chunk.operation_count", chunk.OperationIndices.Count);

            if (instr.CaptureCommandText)
            {
                AttachCommandText(activity, chunk.CommandText, instr.MaxCommandLength);
            }

            return new ChunkScope(activity, instr.RecordException);
        }
        catch
        {
            return null;
        }
    }

    internal static void RecordChunkSkipped(int chunkIndex)
    {
        try
        {
            Activity.Current?.AddEvent(new ActivityEvent(
                "chunk.skipped",
                tags: new ActivityTagsCollection
                {
                    ["nslabs.bulk.chunk.index"] = chunkIndex,
                }));
        }
        catch
        {
            // Best-effort event only.
        }
    }

    internal static void RecordRetryAttempt(int attempt)
    {
        try
        {
            Activity.Current?.AddEvent(new ActivityEvent(
                "retry.attempt",
                tags: new ActivityTagsCollection
                {
                    ["attempt"] = attempt,
                }));
        }
        catch
        {
            // Best-effort event only.
        }
    }

    private static void AttachCommandText(Activity activity, string commandText, int maxLength)
    {
        var text = Truncate(commandText, maxLength, out var truncated);
        activity.SetTag("db.statement", text);
        if (truncated)
        {
            activity.SetTag("nslabs.bulk.command_truncated", true);
        }
    }

    private static void SetError(Activity? activity, Exception exception, bool recordException)
    {
        if (activity is null)
        {
            return;
        }

        try
        {
            activity.SetTag("error.type", exception.GetType().FullName);
            if (recordException)
            {
                // OTel semconv "exception" event. Stack trace is intentionally omitted
                // to keep spans small; full details flow through ILogger error logs.
                activity.AddEvent(new ActivityEvent(
                    "exception",
                    tags: new ActivityTagsCollection
                    {
                        ["exception.type"] = exception.GetType().FullName,
                        ["exception.message"] = exception.Message,
                    }));
            }

            activity.SetStatus(ActivityStatusCode.Error);
        }
        catch
        {
            // Never let telemetry fail the batch.
        }
    }

    internal sealed class BatchScope : IDisposable
    {
        private readonly Activity? _activity;
        private readonly bool _recordException;

        internal BatchScope(Activity? activity, bool recordException)
        {
            _activity = activity;
            _recordException = recordException;
        }

        internal void SetTotalRows(int totalRows)
        {
            try
            {
                _activity?.SetTag("nslabs.bulk.total_rows", totalRows);
            }
            catch
            {
                // Best-effort tag only.
            }
        }

        internal void SetError(Exception exception)
            => BulkExecuteTelemetry.SetError(_activity, exception, _recordException);

        public void Dispose()
        {
            try
            {
                _activity?.Dispose();
            }
            catch
            {
                // Never let telemetry fail the batch.
            }
        }
    }

    internal sealed class ChunkScope : IDisposable
    {
        private readonly Activity? _activity;
        private readonly bool _recordException;

        internal ChunkScope(Activity? activity, bool recordException)
        {
            _activity = activity;
            _recordException = recordException;
        }

        internal void SetRows(int rows)
        {
            try
            {
                _activity?.SetTag("nslabs.bulk.chunk.rows", rows);
            }
            catch
            {
                // Best-effort tag only.
            }
        }

        internal void SetError(Exception exception)
            => BulkExecuteTelemetry.SetError(_activity, exception, _recordException);

        public void Dispose()
        {
            try
            {
                _activity?.Dispose();
            }
            catch
            {
                // Never let telemetry fail the batch.
            }
        }
    }
}
