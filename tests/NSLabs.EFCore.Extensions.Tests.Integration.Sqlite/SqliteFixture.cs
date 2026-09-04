using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Sqlite;

public sealed class SqliteFixture : IAsyncLifetime
{
    private SqliteConnection? _keepAlive;
    public string ConnectionString { get; } = "DataSource=file:nsbulk_sqlite_tests?mode=memory&cache=shared";

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnectionString);
        await _keepAlive.OpenAsync();
        await using var ctx = CreateContext();
        // Ensure SQLite foreign keys etc.
        await ctx.Database.EnsureCreatedAsync();
    }

    public TestDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        return new SqliteTestDbContext(opts);
    }

    public async Task DisposeAsync()
    {
        if (_keepAlive is not null)
        {
            await _keepAlive.DisposeAsync();
            _keepAlive = null;
        }
    }
}

public sealed class SqliteTestDbContext(DbContextOptions<TestDbContext> options) : TestDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Base model is provider-neutral (CreatedAt is a regular property);
        // keep deterministic IDs for seeding.
        modelBuilder.Entity<Item>().Property(x => x.CreatedAt).ValueGeneratedNever();
        // Deterministic IDs
        modelBuilder.Entity<Item>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<AuditLog>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<Pet>().Property(x => x.PetId).ValueGeneratedNever();
        // UNIQUE required for ON CONFLICT tests
        modelBuilder.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Order>().HasIndex(x => x.OrderNo).IsUnique();
    }
}

[CollectionDefinition("sqlite")]
public sealed class SqliteCollection : ICollectionFixture<SqliteFixture>;
