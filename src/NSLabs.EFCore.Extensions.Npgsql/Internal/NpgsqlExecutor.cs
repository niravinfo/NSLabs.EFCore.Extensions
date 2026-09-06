using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NSLabs.EFCore.Extensions.Diagnostics;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class NpgsqlExecutor
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
        // SAFETY S11: presizing preserves per-op counts
        var counts = new Dictionary<int, int>(operations.Count);
        // Initialize zero for all to handle zero-row upsert etc.
        foreach (var op in operations) counts[op.GlobalIndex] = 0;

        var database = context.Database;
        var connection = database.GetDbConnection();
        var transaction = database.CurrentTransaction?.GetDbTransaction();
        var shouldCloseConnection = false;

        // Chunk telemetry context — resolved only when observed (active
        // ActivityListener). Null means defaults; StartChunkScope re-checks.
        BulkInstrumentationOptions? instrumentation = null;
        string? dbSystem = null;
        string? dbName = null;
        if (BulkExecuteTelemetry.Source.HasListeners())
        {
            try
            {
                instrumentation = BulkBatch.ResolveInstrumentationEffective(context);
                dbSystem = BulkExecuteTelemetry.DbSystem(context.Database.ProviderName);
                dbName = connection.Database;
                if (string.IsNullOrEmpty(dbName))
                {
                    dbName = null;
                }
            }
            catch
            {
                instrumentation = null;
                dbSystem = null;
                dbName = null;
            }
        }

        try
        {
            if (closeConnection && connection.State == ConnectionState.Closed)
            {
                await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                shouldCloseConnection = true;
            }

            await ExecuteCoreAsync(connection, transaction, chunks, counts, options, cancellationToken, instrumentation, dbSystem, dbName).ConfigureAwait(false);

            if (options.ThrowIfZeroAffected)
            {
                foreach (var operation in operations)
                {
                    if (counts.TryGetValue(operation.GlobalIndex, out var affected) && affected == 0)
                        throw new BulkZeroRowsAffectedException(operation.GlobalIndex, operation.EntityType.DisplayName());
                }
            }

            return counts;
        }
        finally
        {
            if (shouldCloseConnection && connection.State == ConnectionState.Open)
                await database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    internal static async Task ExecuteCoreAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction,
        IReadOnlyList<SqlChunkPlan> chunks,
        Dictionary<int, int> counts,
        BulkExecuteOptions options,
        CancellationToken cancellationToken,
        BulkInstrumentationOptions? instrumentation = null,
        string? dbSystem = null,
        string? dbName = null)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            await ExecuteChunkAsync(connection, chunks[i], transaction, counts, options, cancellationToken, i, instrumentation, dbSystem, dbName).ConfigureAwait(false);
        }
    }

    internal static async Task ExecuteChunkAsync(
        System.Data.Common.DbConnection connection,
        SqlChunkPlan chunk,
        System.Data.Common.DbTransaction? transaction,
        Dictionary<int, int> counts,
        BulkExecuteOptions options,
        CancellationToken cancellationToken,
        int chunkIndex = 0,
        BulkInstrumentationOptions? instrumentation = null,
        string? dbSystem = null,
        string? dbName = null)
    {
        // Zero-row upsert no-op
        if (chunk.Parameters.Count == 0 && chunk.CommandText.StartsWith("-- zero-row", StringComparison.Ordinal))
        {
            // counts already zero
            BulkExecuteTelemetry.RecordChunkSkipped(chunkIndex);
            return;
        }

        using var scope = BulkExecuteTelemetry.StartChunkScope(chunk, chunkIndex, instrumentation, dbSystem, dbName);
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = chunk.CommandText;
            if (options.CommandTimeout is { } timeout) command.CommandTimeout = timeout;

            foreach (var param in chunk.Parameters)
            {
                var dbParam = command.CreateParameter();
                dbParam.ParameterName = param.Name;
                dbParam.Value = param.Value ?? DBNull.Value;
                command.Parameters.Add(dbParam);
            }

            int rows;
            try
            {
                rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState is "23505" or "42P10")
            {
                // 23505 = unique_violation, 42P10 = no matching UNIQUE constraint for ON CONFLICT target
                throw new InvalidOperationException(
                    $"PostgreSQL ON CONFLICT clause does not match any PRIMARY KEY or UNIQUE constraint, or a duplicate row was attempted. Ensure a UNIQUE index exists on the conflict target columns. PostgreSQL error {ex.SqlState}: {ex.Message}", ex);
            }

            scope?.SetRows(rows);

            // OperationIndices for npgsql per-unit chunks are single element
            foreach (var idx in chunk.OperationIndices)
            {
                counts[idx] = counts.TryGetValue(idx, out var existing) ? existing + rows : rows;
            }
        }
        catch (Exception ex)
        {
            scope?.SetError(ex);
            throw;
        }
    }
}
