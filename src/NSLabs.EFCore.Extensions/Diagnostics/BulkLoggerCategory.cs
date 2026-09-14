namespace NSLabs.EFCore.Extensions.Diagnostics;

/// <summary>
/// Logger category for all bulk-execution events. Filter with
/// <c>optionsBuilder.LogTo(..., new[] { BulkLoggerCategory.Name })</c>.
/// Identical to <see cref="BulkExecuteTelemetryNames.SourceName"/> on purpose:
/// one identity across logs and traces.
/// </summary>
public static class BulkLoggerCategory
{
    /// <summary>Stable category string for all 60000-range bulk events.</summary>
    public const string Name = BulkExecuteTelemetryNames.SourceName;
}
