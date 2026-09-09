namespace NSLabs.EFCore.Extensions;

/// <summary>
/// Observability policy for bulk execution. Configured once at startup via
/// <c>services.AddNSLabsBulkInstrumentation(...)</c> or
/// <see cref="BulkInstrumentation"/>; independent of <see cref="BulkExecuteOptions"/>.
/// An explicit per-call <see cref="BulkExecuteOptions"/> replaces execution
/// configuration only and never resets these settings.
/// </summary>
public sealed class BulkInstrumentationOptions
{
    /// <summary>
    /// Emit one child span per executed chunk. Defaults to <see langword="true"/>.
    /// Disable to reduce span volume on very large batches; the batch span and
    /// <c>chunk.skipped</c> / <c>retry.attempt</c> events are still emitted.
    /// </summary>
    public bool EnableChunkSpans { get; set; } = true;

    /// <summary>
    /// Attach an <c>exception</c> event (type + message, never parameter values)
    /// to spans on failure. Defaults to <see langword="true"/>.
    /// <c>Status=Error</c> is set regardless of this flag.
    /// </summary>
    public bool RecordException { get; set; } = true;

    /// <summary>
    /// Attach (truncated) SQL command text as <c>db.statement</c>. Defaults to
    /// <see langword="false"/>. Never includes parameter values. This flag only
    /// controls whether the library attaches the attribute at all; sampling stays
    /// with the OpenTelemetry SDK sampler and collector redaction still applies.
    /// </summary>
    public bool CaptureCommandText { get; set; }

    /// <summary>
    /// Maximum characters captured per <c>db.statement</c>. Defaults to 4000.
    /// Must be greater than zero.
    /// </summary>
    public int MaxCommandLength { get; set; } = 4000;

    public BulkInstrumentationOptions Clone()
    {
        var clone = new BulkInstrumentationOptions();
        CopyTo(clone);
        return clone;
    }

    public void CopyTo(BulkInstrumentationOptions target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.EnableChunkSpans = EnableChunkSpans;
        target.RecordException = RecordException;
        target.CaptureCommandText = CaptureCommandText;
        target.MaxCommandLength = MaxCommandLength;
    }

    internal void Validate()
    {
        if (MaxCommandLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCommandLength),
                MaxCommandLength,
                $"'{nameof(MaxCommandLength)}' must be greater than zero.");
        }
    }
}
