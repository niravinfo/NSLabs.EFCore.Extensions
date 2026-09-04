using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class SqliteExecutor
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
            return strategy.ExecuteAsync(
                () => RunAsync(context, chunks, operations, options, closeConnection: true, cancellationToken));
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

        try
        {
            if (closeConnection && connection.State == ConnectionState.Closed)
            {
                await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                shouldCloseConnection = true;
            }

            await ExecuteCoreAsync(connection, transaction, chunks, counts, options, cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            await ExecuteChunkAsync(connection, chunk, transaction, counts, options, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task ExecuteChunkAsync(
        System.Data.Common.DbConnection connection,
        SqlChunkPlan chunk,
        System.Data.Common.DbTransaction? transaction,
        Dictionary<int, int> counts,
        BulkExecuteOptions options,
        CancellationToken cancellationToken)
    {
        // Zero-row upsert no-op
        if (chunk.Parameters.Count == 0 && chunk.CommandText.StartsWith("-- zero-row", StringComparison.Ordinal))
        {
            // counts already zero
            return;
        }

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

        options.OnCommandText?.Invoke(chunk.CommandText);

        int rows;
        try
        {
            rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19 || ex.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("ON CONFLICT", StringComparison.OrdinalIgnoreCase))
        {
            // Surface with hint about UNIQUE constraint requirement
            throw new InvalidOperationException(
                $"SQLite ON CONFLICT clause does not match any PRIMARY KEY or UNIQUE constraint. Ensure a UNIQUE index exists on the conflict target columns. SQLite error: {ex.Message}", ex);
        }

        // OperationIndices for sqlite per-unit chunks are single element
        foreach (var idx in chunk.OperationIndices)
        {
            counts[idx] = counts.TryGetValue(idx, out var existing) ? existing + rows : rows;
        }
    }
}
