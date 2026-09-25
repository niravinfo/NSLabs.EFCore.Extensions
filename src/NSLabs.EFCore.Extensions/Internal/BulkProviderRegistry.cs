using System.Collections.Concurrent;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class BulkProviderRegistry
{
    // P5: ConcurrentDictionary — Register and the Resolve reflection fallback both mutate
    // this from arbitrary threads; a plain Dictionary could corrupt under concurrent first-use.
    private static readonly ConcurrentDictionary<string, IBulkProvider> _providers = new(StringComparer.Ordinal);

    // Table-driven well-known providers: EF provider name -> provider implementation
    // (assembly-qualified type name). Adding a new database = one line here.
    private static readonly Dictionary<string, string> KnownProviders = new(StringComparer.Ordinal)
    {
        ["Microsoft.EntityFrameworkCore.SqlServer"] = "NSLabs.EFCore.Extensions.Internal.SqlServerProvider, NSLabs.EFCore.Extensions.SqlServer",
        ["Microsoft.EntityFrameworkCore.Sqlite"] = "NSLabs.EFCore.Extensions.Internal.SqliteProvider, NSLabs.EFCore.Extensions.Sqlite",
        ["Npgsql.EntityFrameworkCore.PostgreSQL"] = "NSLabs.EFCore.Extensions.Internal.NpgsqlProvider, NSLabs.EFCore.Extensions.Npgsql",
    };

    public static void Register(IBulkProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.ProviderName] = provider;
    }

    public static IBulkProvider? Resolve(string? providerName)
    {
        if (providerName is not null && _providers.TryGetValue(providerName, out var existing))
        {
            return existing;
        }

        // Fallback: try to load known provider via reflection (covers case where ModuleInitializer
        // hasn't run because assembly hasn't been touched yet, but is referenced).
        // Double-check + GetOrAdd: the dictionary is only mutated with a successful instance
        // (failed TryLoad is not cached, preserving retry semantics); losers of the race
        // discard their instance and use the winner's.
        if (providerName is not null && KnownProviders.TryGetValue(providerName, out var typeName))
        {
            if (_providers.TryGetValue(providerName, out var raced))
            {
                return raced;
            }

            var loaded = TryLoad(typeName);
            if (loaded is not null)
            {
                return _providers.GetOrAdd(providerName, loaded);
            }
        }

        return null;
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
