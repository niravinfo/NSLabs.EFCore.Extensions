using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace NSLabs.EFCore.Extensions.DependencyInjection;

public sealed class BulkInstrumentationOptionsExtension : IDbContextOptionsExtension
{
    private readonly BulkInstrumentationOptions _options;

    public BulkInstrumentationOptionsExtension()
        : this(new BulkInstrumentationOptions())
    {
    }

    public BulkInstrumentationOptionsExtension(BulkInstrumentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Copy-in: never store the caller-owned instance. The extension is shared across
        // pooled contexts and threads, so the stored snapshot must be privately owned.
        // Validated here (not per-execution): the snapshot is immutable after this point,
        // so re-validating it on every ExecuteAsync would always yield the same result.
        _options = options.Clone();
        _options.Validate();
    }

    public BulkInstrumentationOptions Options => _options.Clone();

    internal BulkInstrumentationOptions SnapshotRef => _options;

    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    // Intentionally a no-op: resolution goes through FindExtension (see
    // BulkBatch.ResolveInstrumentationEffective), not through an internal singleton.
    // Registering the options as a service would require GetServiceProviderHashCode
    // to vary with option values, otherwise AddDbContextPool could reuse one provider
    // (and one stale snapshot) across different option values.
    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options) => _options.Validate();

    private sealed class ExtensionInfo(BulkInstrumentationOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
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
                var options = ((BulkInstrumentationOptionsExtension)Extension).SnapshotRef;
                return $"BulkInstrumentation EnableChunkSpans={options.EnableChunkSpans} RecordException={options.RecordException} CaptureCommandText={options.CaptureCommandText} MaxCommandLength={options.MaxCommandLength} ";
            }
        }

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            var options = ((BulkInstrumentationOptionsExtension)Extension).SnapshotRef;
            debugInfo["BulkInstrumentation:EnableChunkSpans"] = options.EnableChunkSpans.ToString();
            debugInfo["BulkInstrumentation:RecordException"] = options.RecordException.ToString();
            debugInfo["BulkInstrumentation:CaptureCommandText"] = options.CaptureCommandText.ToString();
            debugInfo["BulkInstrumentation:MaxCommandLength"] = options.MaxCommandLength.ToString();
        }
    }
}
