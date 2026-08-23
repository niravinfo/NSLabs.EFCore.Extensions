using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using EF.Core.Extensions;

namespace EF.Core.Extensions.Internal;

internal static class SqlServerExecutor
{
    public static Task<Dictionary<int, int>> ExecuteAsync(
        DbContext context,
        IReadOnlyList<SqlChunkPlan> chunks,
        BulkExecuteOptions options,
        CancellationToken cancellationToken)
    {
        var database = context.Database;

        if (database.CurrentTransaction is not null)
        {
            return RunAsync(context, chunks, options, ownTransaction: false, closeConnection: false, cancellationToken);
        }

        var strategy = database.CreateExecutionStrategy();

        if (strategy.RetriesOnFailure)
        {
            return strategy.ExecuteAsync(
                () => RunAsync(context, chunks, options, ownTransaction: options.AutoTransaction, closeConnection: true, cancellationToken));
        }

        return RunAsync(context, chunks, options, ownTransaction: options.AutoTransaction, closeConnection: true, cancellationToken);
    }

    private static async Task<Dictionary<int, int>> RunAsync(
        DbContext context,
        IReadOnlyList<SqlChunkPlan> chunks,
        BulkExecuteOptions options,
        bool ownTransaction,
        bool closeConnection,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<int, int>();
        var database = context.Database;
        IDbContextTransaction? ownedTransaction = null;

        try
        {
            if (ownTransaction)
            {
                await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                ownedTransaction = await database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }

            var transaction = database.CurrentTransaction?.GetDbTransaction();

            foreach (var chunk in chunks)
            {
                await ExecuteChunkAsync(database.GetDbConnection(), chunk, transaction, counts, options, cancellationToken).ConfigureAwait(false);
            }

            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return counts;
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                try
                {
                    await ownedTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            }

            if (closeConnection && database.GetDbConnection().State == ConnectionState.Open && ownedTransaction is not null)
            {
                await database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteChunkAsync(
        System.Data.Common.DbConnection connection,
        SqlChunkPlan chunk,
        System.Data.Common.DbTransaction? transaction,
        Dictionary<int, int> counts,
        BulkExecuteOptions options,
        CancellationToken cancellationToken)
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

        options.OnCommandText?.Invoke(chunk.CommandText);

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

        for (var k = 0; k < chunk.OperationIndices.Count; k++)
        {
            counts[chunk.OperationIndices[k]] = reader.GetInt32(k);
        }
    }
}
