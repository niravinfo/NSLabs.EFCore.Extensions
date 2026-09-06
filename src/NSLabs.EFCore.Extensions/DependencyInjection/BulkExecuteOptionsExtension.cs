using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace NSLabs.EFCore.Extensions.DependencyInjection;

public sealed class BulkExecuteOptionsExtension : IDbContextOptionsExtension
{
    private readonly BulkExecuteOptions _options;

    public BulkExecuteOptionsExtension()
        : this(new BulkExecuteOptions())
    {
    }

    public BulkExecuteOptionsExtension(BulkExecuteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Copy-in: never store the caller-owned instance. The extension is shared across
        // pooled contexts and threads, so the stored snapshot must be privately owned.
        // Validated here (not per-execution): the snapshot is immutable after this point,
        // so re-validating it on every ExecuteAsync would always yield the same result.
        _options = options.Clone();
        _options.Validate();
    }

    public BulkExecuteOptions Options => _options.Clone();

    internal BulkExecuteOptions SnapshotRef => _options;

    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    // Intentionally a no-op: resolution goes through FindExtension (see BulkBatch.ResolveEffective),
    // not through an internal singleton. Registering the options as a service would require
    // GetServiceProviderHashCode to vary with option values, otherwise AddDbContextPool could
    // reuse one provider (and one stale snapshot) across different option values.
    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options) => _options.Validate();

    private sealed class ExtensionInfo(BulkExecuteOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        // No services are registered by ApplyServices, so no option value can require a
        // new internal service provider: hash is constant and any two instances share.
        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo;

        public override string LogFragment
        {
            get
            {
                var options = ((BulkExecuteOptionsExtension)Extension).SnapshotRef;
                return $"BulkExecute MaxParametersPerCommand={options.MaxParametersPerCommand} ThrowIfZeroAffected={options.ThrowIfZeroAffected} CommandTimeout={options.CommandTimeout?.ToString() ?? "null"} ";
            }
        }

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            var options = ((BulkExecuteOptionsExtension)Extension).SnapshotRef;
            debugInfo["BulkExecute:MaxParametersPerCommand"] = options.MaxParametersPerCommand.ToString();
            debugInfo["BulkExecute:ThrowIfZeroAffected"] = options.ThrowIfZeroAffected.ToString();
            debugInfo["BulkExecute:CommandTimeout"] = options.CommandTimeout?.ToString() ?? "null";
        }
    }
}
