using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace NSLabs.EFCore.Extensions.Diagnostics;

/// <summary>
/// MEL helpers for bulk logging. Resolution is level-agnostic: a non-null return
/// means "a factory exists", never "logging is enabled". Every log site checks
/// its own event's level with <c>IsEnabled</c>, so Warning/Error-only configs
/// and future Debug events all behave correctly.
/// </summary>
internal static class BulkExecuteLogging
{
    /// <summary>
    /// Resolves the bulk-category logger once per batch. Returns null only when
    /// no factory is configured or lookup fails; never throws.
    /// </summary>
    public static ILogger? ResolveBulkLogger(DbContext context)
    {
        try
        {
            return context.GetService<ILoggerFactory>()?.CreateLogger(BulkLoggerCategory.Name);
        }
        catch
        {
            return null;
        }
    }
}
