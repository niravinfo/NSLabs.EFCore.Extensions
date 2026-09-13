using Microsoft.Extensions.Logging;

namespace NSLabs.EFCore.Extensions.Diagnostics;

/// <summary>
/// Source-generated log messages for bulk execution (Phase 1: batch-level only).
/// All call sites must guard with <c>logger.IsEnabled(level)</c> before calling,
/// so argument evaluation (counts, database name) is skipped when disabled.
/// </summary>
internal static partial class BulkExecuteLoggingDefinitions
{
    [LoggerMessage(
        EventId = BulkEventId.BulkExecuteStartingId,
        Level = LogLevel.Debug,
        Message = "BulkExecute starting: {OperationCount} operation(s) ({UpdateCount} update, {UpsertCount} upsert, {DeleteCount} delete) in {ChunkCount} chunk(s) on {Provider} database {Database}.")]
    internal static partial void BulkExecuteStarting(
        ILogger logger,
        int operationCount,
        int updateCount,
        int upsertCount,
        int deleteCount,
        int chunkCount,
        string? provider,
        string? database);

    [LoggerMessage(
        EventId = BulkEventId.BulkExecuteExecutedId,
        Level = LogLevel.Information,
        Message = "BulkExecute completed: {TotalRows} row(s) across {OperationCount} operation(s) in {ChunkCount} chunk(s) on {Provider} database {Database} in {ElapsedMs}ms.")]
    internal static partial void BulkExecuteExecuted(
        ILogger logger,
        int operationCount,
        int totalRows,
        long elapsedMs,
        int chunkCount,
        string? provider,
        string? database);

    [LoggerMessage(
        EventId = BulkEventId.BulkExecuteFailedId,
        Level = LogLevel.Error,
        Message = "BulkExecute failed after {OperationCount} operation(s) on {Provider}.")]
    internal static partial void BulkExecuteFailed(
        ILogger logger,
        Exception exception,
        int operationCount,
        string? provider);

    [LoggerMessage(
        EventId = BulkEventId.BulkChunkExecutingId,
        Level = LogLevel.Information,
        Message = "Bulk chunk {ChunkIndex} executing: {OperationCount} operation(s), {ParameterCount} parameter(s). {CommandText}")]
    internal static partial void BulkChunkExecuting(
        ILogger logger,
        int chunkIndex,
        int operationCount,
        int parameterCount,
        string commandText);

    [LoggerMessage(
        EventId = BulkEventId.BulkChunkExecutedId,
        Level = LogLevel.Information,
        Message = "Bulk chunk {ChunkIndex} executed: {RowsAffected} row(s) in {ElapsedMs}ms.")]
    internal static partial void BulkChunkExecuted(
        ILogger logger,
        int chunkIndex,
        int rowsAffected,
        long elapsedMs);
}
