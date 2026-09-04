namespace NSLabs.EFCore.Extensions.Internal;

internal static class BulkProviderRegistry
{
    private static readonly Dictionary<string, IBulkProvider> _providers = new(StringComparer.Ordinal);

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
        if (providerName is not null && KnownProviders.TryGetValue(providerName, out var typeName))
        {
            var loaded = TryLoad(typeName);
            if (loaded is not null)
            {
                _providers[providerName] = loaded;
                return loaded;
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
