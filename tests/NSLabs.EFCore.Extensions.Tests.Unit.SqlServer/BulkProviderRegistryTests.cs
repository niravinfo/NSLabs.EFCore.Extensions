using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

public class BulkProviderRegistryTests
{
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private sealed class FakeProvider(string providerName) : IBulkProvider
    {
        public string ProviderName { get; } = providerName;

        public IReadOnlyList<SqlChunkPlan> Generate(
            IReadOnlyList<BoundOperation> operations,
            int maxParametersPerCommand,
            DbContext context)
            => throw new NotSupportedException();

        public Task<Dictionary<int, int>> ExecuteAsync(
            DbContext context,
            IReadOnlyList<SqlChunkPlan> chunks,
            IReadOnlyList<BoundOperation> operations,
            BulkExecuteOptions options,
            ILogger? logger,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    [Fact]
    public void Resolve_returns_null_for_unknown_and_null_provider_names()
    {
        Assert.Null(BulkProviderRegistry.Resolve("Contoso.SomeUnknownProvider"));
        Assert.Null(BulkProviderRegistry.Resolve(null));
    }

    [Fact]
    public void Resolve_returns_the_instance_passed_to_Register()
    {
        var provider = new FakeProvider("Fake.Provider.Registered");

        BulkProviderRegistry.Register(provider);

        Assert.Same(provider, BulkProviderRegistry.Resolve("Fake.Provider.Registered"));
    }

    [Fact]
    public void Register_rejects_a_null_provider()
    {
        Assert.Throws<ArgumentNullException>(() => BulkProviderRegistry.Register(null!));
    }

    [Fact]
    public void Failed_fallback_lookup_stays_not_found_and_does_not_poison_other_lookups()
    {
        // The Npgsql provider assembly is not referenced by this test project, so the
        // reflection fallback cannot load it. Resolution must keep reporting "not found"
        // on every call (a failed load must not be cached as a permanent negative, or a
        // late-loading provider assembly would never be found again) and must leave
        // unrelated lookups working.
        Assert.Null(BulkProviderRegistry.Resolve(NpgsqlProviderName));
        Assert.Null(BulkProviderRegistry.Resolve(NpgsqlProviderName));
        Assert.Null(BulkProviderRegistry.Resolve(NpgsqlProviderName));

        Assert.NotNull(BulkProviderRegistry.Resolve(SqlServerProviderName));
    }

    [Fact]
    public async Task Concurrent_register_and_resolve_never_lose_or_corrupt_entries()
    {
        const int workers = 32;
        const int perWorker = 250;

        // Interleave writes (Register) and reads (Resolve) across threads. Enough distinct
        // keys to force repeated table resizes, which is where an unsynchronized Dictionary
        // drops entries or hands back a torn read.
        await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                var name = $"Fake.Provider.Race.{worker}.{i}";
                BulkProviderRegistry.Register(new FakeProvider(name));
                _ = BulkProviderRegistry.Resolve(name);
            }
        })));

        // Every name registered during the storm must still resolve to a self-consistent
        // provider afterwards.
        for (var worker = 0; worker < workers; worker++)
        {
            for (var i = 0; i < perWorker; i++)
            {
                var name = $"Fake.Provider.Race.{worker}.{i}";
                var resolved = BulkProviderRegistry.Resolve(name);

                Assert.NotNull(resolved);
                Assert.Equal(name, resolved!.ProviderName);
            }
        }
    }

    [Fact]
    public async Task Concurrent_resolve_of_a_loaded_provider_returns_one_shared_instance()
    {
        const int workers = 32;
        const int iterations = 500;

        var results = await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            var seen = new IBulkProvider[iterations];
            for (var i = 0; i < iterations; i++)
            {
                seen[i] = BulkProviderRegistry.Resolve(SqlServerProviderName)!;
            }

            return seen;
        })));

        var all = results.SelectMany(r => r).ToArray();

        Assert.DoesNotContain(all, provider => provider is null);
        Assert.Single(all.Distinct());
    }
}
