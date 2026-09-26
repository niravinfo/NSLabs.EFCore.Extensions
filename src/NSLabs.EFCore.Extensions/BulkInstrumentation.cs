namespace NSLabs.EFCore.Extensions;

/// <summary>
/// Process-wide observability policy for bulk execution. Configured once at
/// startup via <c>services.AddNSLabsBulkInstrumentation(...)</c> or
/// <see cref="Configure(Action{BulkInstrumentationOptions})"/>; independent of
/// <see cref="BulkExecuteOptions"/> and deliberately separate from
/// <c>DbContext</c> configuration. An explicit per-call
/// <see cref="BulkExecuteOptions"/> replaces execution configuration only and
/// never resets these settings.
/// </summary>
/// <remarks>
/// Thread-safe. <see cref="Configure(Action{BulkInstrumentationOptions})"/>
/// copies the values in and validates them; later mutations of the caller's
/// instance are never observed. Resolution returns the current snapshot
/// reference directly (never mutated after publish, only replaced).
/// </remarks>
public static class BulkInstrumentation
{
    private static readonly object s_lock = new();

    // Volatile: s_current is only ever *replaced* (never mutated after publish), so a
    // release/acquire read is sufficient and correct. This keeps the per-batch
    // SnapshotRef/Current read off the lock; the lock remains only where read-modify-write
    // atomicity is actually required (Configure's clone-mutate-publish sequence).
    private static volatile BulkInstrumentationOptions s_current = new();

    /// <summary>
    /// Current process-wide policy snapshot. Never <see langword="null"/>.
    /// Do not mutate the returned instance; call <c>Configure</c> to replace it.
    /// </summary>
    public static BulkInstrumentationOptions Current => s_current;

    internal static BulkInstrumentationOptions SnapshotRef => s_current;

    /// <summary>
    /// Replaces the process-wide policy. The action receives a copy of the
    /// current policy so repeated calls are additive.
    /// </summary>
    public static void Configure(Action<BulkInstrumentationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        lock (s_lock)
        {
            var options = s_current.Clone();
            configure(options);
            options.Validate();
            s_current = options;
        }
    }

    /// <summary>
    /// Replaces the process-wide policy. The instance is copied in and
    /// validated; later mutations of the caller's instance are never observed.
    /// </summary>
    public static void Configure(BulkInstrumentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (s_lock)
        {
            var snapshot = options.Clone();
            snapshot.Validate();
            s_current = snapshot;
        }
    }

    /// <summary>
    /// Restores factory defaults. Intended for tests; applications configure once.
    /// </summary>
    public static void Reset()
    {
        lock (s_lock)
        {
            s_current = new BulkInstrumentationOptions();
        }
    }
}
