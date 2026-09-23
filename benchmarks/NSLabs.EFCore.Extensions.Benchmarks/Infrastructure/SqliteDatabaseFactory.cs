using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Benchmarks.Infrastructure;

/// <summary>
/// Shared-cache named in-memory SQLite database with a keep-alive connection,
/// matching the integration-test fixture pattern so every context sees one database.
/// </summary>
internal sealed class SqliteDatabaseFactory : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    public SqliteDatabaseFactory(int seedItemCount)
    {
        _keepAlive = new SqliteConnection("DataSource=file:nsbulk_benchmarks?mode=memory&cache=shared");
        _keepAlive.Open();

        Options = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseSqlite(_keepAlive)
            .Options;

        using var context = new BenchmarkDbContext(Options);
        context.Database.EnsureCreated();
        SeedItems(context, seedItemCount);
    }

    public DbContextOptions<BenchmarkDbContext> Options { get; }

    public BenchmarkDbContext CreateContext() => new(Options);

    public static List<BenchItem> CreateItems(int count)
    {
        var items = new List<BenchItem>(count);
        for (var i = 1; i <= count; i++)
        {
            items.Add(new BenchItem
            {
                Id = i,
                Key1 = $"key-{i}",
                Key2 = i,
                Key3 = i * 2,
                Active = true,
            });
        }

        return items;
    }

    private static void SeedItems(BenchmarkDbContext context, int count)
    {
        if (count <= 0)
        {
            return;
        }

        context.Items.AddRange(CreateItems(count));
        context.SaveChanges();
    }

    public void Dispose() => _keepAlive.Dispose();
}
