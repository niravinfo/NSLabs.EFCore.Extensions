using System.Collections.Concurrent;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class BulkProviderRegistry
{
    // ConcurrentDictionary: Register (provider [ModuleInitializer]) and the Resolve fallback
    // below both write, and two threads can race here before the module initializer has run.
    // Values are Lazy so that exactly one thread performs the reflection load -- note that
    // ConcurrentDictionary.GetOrAdd(key, factory) does NOT serialize the factory, so it would
    // still let several threads call Activator.CreateInstance concurrently.
    private static readonly ConcurrentDictionary<string, Lazy<IBulkProvider?>> _providers =
        new(StringComparer.Ordinal);

    // Table-driven well-known providers: EF provider name -> provider implementation
    // (assembly-qualified type name). Adding a new database = one line here.
    // Read-only after static initialization, so a plain Dictionary is safe.
    private static readonly Dictionary<string, string> KnownProviders = new(StringComparer.Ordinal)
    {
        ["Microsoft.EntityFrameworkCore.SqlServer"] = "NSLabs.EFCore.Extensions.Internal.SqlServerProvider, NSLabs.EFCore.Extensions.SqlServer",
        ["Microsoft.EntityFrameworkCore.Sqlite"] = "NSLabs.EFCore.Extensions.Internal.SqliteProvider, NSLabs.EFCore.Extensions.Sqlite",
        ["Npgsql.EntityFrameworkCore.PostgreSQL"] = "NSLabs.EFCore.Extensions.Internal.NpgsqlProvider, NSLabs.EFCore.Extensions.Npgsql",
    };

    public static void Register(IBulkProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // Wrap the already-constructed instance: no user code runs under the dictionary lock.
        _providers[provider.ProviderName] =
            new Lazy<IBulkProvider?>(() => provider, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static IBulkProvider? Resolve(string? providerName)
    {
        if (providerName is null)
        {
            return null;
        }

        if (_providers.TryGetValue(providerName, out var existing))
        {
            return Materialize(providerName, existing);
        }

        // Fallback: try to load known provider via reflection (covers case where ModuleInitializer
        // hasn't run because assembly hasn't been touched yet, but is referenced).
        if (KnownProviders.TryGetValue(providerName, out var typeName))
        {
            var lazy = _providers.GetOrAdd(
                providerName,
                static name => new Lazy<IBulkProvider?>(
                    () => TryLoad(KnownProviders[name]),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return Materialize(providerName, lazy);
        }

        return null;
    }

    private static IBulkProvider? Materialize(string providerName, Lazy<IBulkProvider?> lazy)
    {
        var provider = lazy.Value;
        if (provider is null)
        {
            // A failed load must not become a permanent negative: the provider assembly may
            // not have been loadable yet. Evict so the next Resolve retries.
            //
            // Compare-and-remove (not a plain TryRemove): a concurrent Register could have
            // installed a valid provider under this name after we read the lazy, and an
            // unconditional remove would throw that registration away.
            _providers.TryRemove(new KeyValuePair<string, Lazy<IBulkProvider?>>(providerName, lazy));
        }

        return provider;
    }

    private static IBulkProvider? TryLoad(string typeName)
    {
        try
        {
            var type = Type.GetType(typeName, throwOnError: false);
            if (type is null)
            {
                return null;
            }

            return Activator.CreateInstance(type) as IBulkProvider;
        }
        catch
        {
            return null;
        }
    }
}
