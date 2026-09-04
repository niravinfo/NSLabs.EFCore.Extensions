namespace NSLabs.EFCore.Extensions.Tests.Integration.Sqlite;

[Collection("sqlite")]
public abstract class SqliteTestBase(SqliteFixture fixture)
{
    protected SqliteFixture Fixture { get; } = fixture;
}
