using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Testcontainers.MsSql;

namespace NSLabs.EFCore.Extensions.Tests.Integration.SqlServer;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .WithEnvironment("ACCEPT_EULA", "Y")
                .Build();

            await _container.StartAsync();

            var connectionStringBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_container.GetConnectionString())
            {
                InitialCatalog = "NSLabsBulkTests"
            };
            ConnectionString = connectionStringBuilder.ConnectionString;

            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
        }
        catch (Exception ex)
        {
            UnavailableReason = $"SQL Server test container is unavailable: {ex.Message}";
            await DisposeAsync();
        }
    }

    public TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new IntegrationTestDbContext(options);
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

/// <summary>
/// Integration variant of the shared test model. Integration tests seed deterministic
/// explicit IDs, so identity generation is disabled for keys that are always assigned
/// explicitly (Customer.Id stays an identity column because upsert tests rely on it).
/// </summary>
public sealed class IntegrationTestDbContext(DbContextOptions<TestDbContext> options) : TestDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Store-generated column (lives here, not in the neutral base model).
        modelBuilder.Entity<Item>().Property(x => x.CreatedAt).HasComputedColumnSql("GETDATE()");
        modelBuilder.Entity<Item>().Property(x => x.Id).ValueGeneratedNever()
            .HasAnnotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.None);
        modelBuilder.Entity<AuditLog>().Property(x => x.Id).ValueGeneratedNever()
            .HasAnnotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.None);
        modelBuilder.Entity<Pet>().Property(x => x.PetId).ValueGeneratedNever()
            .HasAnnotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.None);
    }
}

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;
