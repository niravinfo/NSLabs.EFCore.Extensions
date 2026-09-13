using System.Data;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NSLabs.EFCore.Extensions;
using NSLabs.EFCore.Extensions.Diagnostics;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class SqlServerExecutor
{
    public static Task<Dictionary<int, int>> ExecuteAsync(
        DbContext context,
        IReadOnlyList<SqlChunkPlan> chunks,
        IReadOnlyList<BoundOperation> operations,
        BulkExecuteOptions options,
        CancellationToken cancellationToken)
    {
        var database = context.Database;

        if (database.CurrentTransaction is not null)
        {
            return RunAsync(context, chunks, operations, options, closeConnection: false, cancellationToken);
        }

        var strategy = database.CreateExecutionStrategy();

        if (strategy.RetriesOnFailure)
        {
            var attempt = 0;
            return strategy.ExecuteAsync(
                () =>
                {
                    if (attempt > 0)
                    {
                        BulkExecuteTelemetry.RecordRetryAttempt(attempt);
                    }

                    attempt++;
                    return RunAsync(context, chunks, operations, options, closeConnection: true, cancellationToken);
                });
        }

        return RunAsync(context, chunks, operations, options, closeConnection: true, cancellationToken);
    }

    private static async Task<Dictionary<int, int>> RunAsync(
        DbContext context,
        IReadOnlyList<SqlChunkPlan> chunks,
        IReadOnlyList<BoundOperation> operations,
        BulkExecuteOptions options,
        bool closeConnection,
        CancellationToken cancellationToken)
    {
        // SAFETY S11: presizing preserves per-op @@ROWCOUNT accumulation
        var counts = new Dictionary<int, int>(operations.Count);
        var database = context.Database;
        var connection = database.GetDbConnection();
        var transaction = database.CurrentTransaction?.GetDbTransaction();
        var shouldCloseConnection = false;

        // Telemetry context — null when not observed (active ActivityListener required).
        // Null means defaults; StartChunkScope re-checks.
        var telemetryContext = TelemetryContext.Resolve(context, connection);

        // Bulk logger — null when no factory is configured. Level checks happen
        // per chunk event, so this stays level-agnostic.
        var logger = BulkExecuteLogging.ResolveBulkLogger(context);

        try
        {
            if (closeConnection && connection.State == ConnectionState.Closed)
            {
                await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                shouldCloseConnection = true;
            }

            await ExecuteCoreAsync(connection, transaction, chunks, counts, options, telemetryContext, logger, cancellationToken).ConfigureAwait(false);

            // When ThrowIfZeroAffected is true and an ambient transaction is present the caller
            // can roll back atomically. Without an ambient transaction each statement has already
            // committed via SQL Server's implicit per-statement transaction, so the exception
            // informs the caller but cannot undo prior chunks. The caller controls rollback via
            // Database.BeginTransactionAsync().
            if (options.ThrowIfZeroAffected)
            {
                foreach (var operation in operations)
                {
                    if (counts.TryGetValue(operation.GlobalIndex, out var affected) && affected == 0)
                    {
                        throw new BulkZeroRowsAffectedException(operation.GlobalIndex, operation.EntityType.DisplayName());
                    }
                }
            }

            return counts;
        }
        finally
        {
            if (shouldCloseConnection && connection.State == ConnectionState.Open)
            {
                await database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    internal static async Task ExecuteCoreAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction,
        IReadOnlyList<SqlChunkPlan> chunks,
        Dictionary<int, int> counts,
        BulkExecuteOptions options,
        TelemetryContext? telemetryContext = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            await ExecuteChunkAsync(connection, chunks[i], transaction, counts, options, i, telemetryContext, logger, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task ExecuteChunkAsync(
        System.Data.Common.DbConnection connection,
        SqlChunkPlan chunk,
        System.Data.Common.DbTransaction? transaction,
        Dictionary<int, int> counts,
        BulkExecuteOptions options,
        int chunkIndex = 0,
        TelemetryContext? telemetryContext = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = BulkExecuteTelemetry.StartChunkScope(chunk, chunkIndex, telemetryContext);

        var isChunkLoggingEnabled = logger?.IsEnabled(LogLevel.Information) == true;
        Stopwatch? chunkStopwatch = null;
        if (isChunkLoggingEnabled)
        {
            BulkExecuteLoggingDefinitions.BulkChunkExecuting(
                logger!, chunkIndex, chunk.OperationIndices.Count, chunk.Parameters.Count, chunk.CommandText);
            chunkStopwatch = Stopwatch.StartNew();
        }

        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = chunk.CommandText;

            if (options.CommandTimeout is { } timeout)
            {
                command.CommandTimeout = timeout;
            }

            foreach (var param in chunk.Parameters)
            {
                var dbParam = command.CreateParameter();
                dbParam.ParameterName = param.Name;
                dbParam.Value = param.Value ?? DBNull.Value;
                command.Parameters.Add(dbParam);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
            }

            if (reader.FieldCount == 0)
            {
                throw new InvalidOperationException("Bulk execution did not return the expected rowcount result set.");
            }

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Bulk execution rowcount result set was empty.");
            }

            var chunkRows = 0;
            for (var k = 0; k < chunk.OperationIndices.Count; k++)
            {
                var index = chunk.OperationIndices[k];
                var affected = reader.GetInt32(k);

                // An operation split across chunks accumulates its rowcount.
                counts[index] = counts.TryGetValue(index, out var existing) ? existing + affected : affected;
                chunkRows += affected;
            }

            scope?.SetRows(chunkRows);

            if (isChunkLoggingEnabled)
            {
                BulkExecuteLoggingDefinitions.BulkChunkExecuted(logger!, chunkIndex, chunkRows, chunkStopwatch!.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            scope?.SetError(ex);
            throw;
        }
    }
}
