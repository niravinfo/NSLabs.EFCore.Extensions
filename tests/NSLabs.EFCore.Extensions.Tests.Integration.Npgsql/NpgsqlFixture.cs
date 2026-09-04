using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Npgsql;

public sealed class NpgsqlFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17")
                .WithDatabase("NSLabsBulkTests")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            await _container.StartAsync();

            ConnectionString = _container.GetConnectionString();

            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
        }
        catch (Exception ex)
        {
            UnavailableReason = $"PostgreSQL test container is unavailable: {ex.Message}";
            await DisposeAsync();
        }
    }

    public TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new NpgsqlTestDbContext(options);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }
}

public sealed class NpgsqlTestDbContext(DbContextOptions<TestDbContext> options) : TestDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Deterministic IDs for seeding (same pattern as Sqlite fixture).
        modelBuilder.Entity<Item>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<Item>().Property(x => x.CreatedAt).ValueGeneratedNever();
        modelBuilder.Entity<AuditLog>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<Pet>().Property(x => x.PetId).ValueGeneratedNever();
        // UNIQUE required for ON CONFLICT tests
        modelBuilder.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Order>().HasIndex(x => x.OrderNo).IsUnique();
    }
}

[CollectionDefinition("npgsql")]
public sealed class NpgsqlCollection : ICollectionFixture<NpgsqlFixture>;
