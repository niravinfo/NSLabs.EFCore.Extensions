using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace NSLabs.EFCore.Extensions.Tests.Integration;

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

        return new TestDbContext(options);
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

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;
