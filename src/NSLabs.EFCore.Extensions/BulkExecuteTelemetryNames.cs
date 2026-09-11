namespace NSLabs.EFCore.Extensions;

/// <summary>
/// Well-known OpenTelemetry identifiers emitted by this library.
/// Register them with the SDK (<c>TracerProviderBuilder.AddSource(SourceName)</c>);
/// no <c>OpenTelemetry.*</c> package reference is required to emit.
/// </summary>
public static class BulkExecuteTelemetryNames
{
    /// <summary>
    /// <see cref="System.Diagnostics.ActivitySource"/> name for bulk-execution spans
    /// (<c>BulkExecute</c> batch spans and <c>BulkExecute.Chunk</c> child spans).
    /// </summary>
    public const string SourceName = "NSLabs.EFCore.Extensions";
}
