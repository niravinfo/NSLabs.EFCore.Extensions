using Microsoft.Extensions.DependencyInjection;
using NSLabs.EFCore.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Separate startup configuration for bulk-execution observability.
/// Call once in <c>Program.cs</c>; deliberately independent of
/// <c>DbContext</c> configuration.
/// </summary>
public static class BulkInstrumentationServiceExtensions
{
    /// <summary>
    /// Sets the process-wide <see cref="BulkInstrumentation"/> policy.
    /// </summary>
    public static IServiceCollection AddNSLabsBulkInstrumentation(
        this IServiceCollection services,
        Action<BulkInstrumentationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            BulkInstrumentation.Configure(configure);
        }

        return services;
    }
}
