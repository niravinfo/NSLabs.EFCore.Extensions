namespace NSLabs.EFCore.Extensions.Internal;

internal static class BulkProviderRegistry
{
    private static readonly Dictionary<string, IBulkProvider> _providers = new(StringComparer.Ordinal);

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
        if (providerName == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            var loaded = TryLoadSqlServerProvider();
            if (loaded is not null)
            {
                _providers[providerName] = loaded;
                return loaded;
            }
        }

        return null;
    }

    private static IBulkProvider? TryLoadSqlServerProvider()
    {
        try
        {
            const string typeName = "NSLabs.EFCore.Extensions.Internal.SqlServerProvider, NSLabs.EFCore.Extensions.SqlServer";
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
