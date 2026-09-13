using Microsoft.Extensions.Logging;

namespace NSLabs.EFCore.Extensions.Diagnostics;

/// <summary>
/// Stable event IDs for bulk-execution logs. All IDs are ≥ <see cref="BaseId"/> (60000),
/// clear of EF Core bands (core ≥ 10000, relational ≥ 20000, providers ≥ 30000,
/// provider design-time ≥ 35000). IDs are stable forever: new events append, never reuse.
/// All events use <see cref="BulkLoggerCategory.Name"/>.
/// </summary>
public static class BulkEventId
{
    /// <summary>Base of the library-reserved range.</summary>
    public const int BaseId = 60000;

    /// <summary>Batch validated, chunks planned, before first round-trip.</summary>
    public const int BulkExecuteStartingId = 60000;

    /// <summary>Batch completed.</summary>
    public const int BulkExecuteExecutedId = 60001;

    /// <summary>Before each chunk command (Phase 2).</summary>
    public const int BulkChunkExecutingId = 60002;

    /// <summary>After each chunk (Phase 2).</summary>
    public const int BulkChunkExecutedId = 60003;

    /// <summary>Exception escapes, including <see cref="BulkZeroRowsAffectedException"/>.</summary>
    public const int BulkExecuteFailedId = 60004;

    /// <summary>Execution-strategy retry attempt (Phase 2).</summary>
    public const int BulkExecuteRetryingId = 60005;

    /// <summary>60000 BulkExecuteStarting.</summary>
    public static readonly EventId BulkExecuteStarting = new(BulkExecuteStartingId, nameof(BulkExecuteStarting));

    /// <summary>60001 BulkExecuteExecuted.</summary>
    public static readonly EventId BulkExecuteExecuted = new(BulkExecuteExecutedId, nameof(BulkExecuteExecuted));

    /// <summary>60002 BulkChunkExecuting.</summary>
    public static readonly EventId BulkChunkExecuting = new(BulkChunkExecutingId, nameof(BulkChunkExecuting));

    /// <summary>60003 BulkChunkExecuted.</summary>
    public static readonly EventId BulkChunkExecuted = new(BulkChunkExecutedId, nameof(BulkChunkExecuted));

    /// <summary>60004 BulkExecuteFailed.</summary>
    public static readonly EventId BulkExecuteFailed = new(BulkExecuteFailedId, nameof(BulkExecuteFailed));

    /// <summary>60005 BulkExecuteRetrying.</summary>
    public static readonly EventId BulkExecuteRetrying = new(BulkExecuteRetryingId, nameof(BulkExecuteRetrying));
}
