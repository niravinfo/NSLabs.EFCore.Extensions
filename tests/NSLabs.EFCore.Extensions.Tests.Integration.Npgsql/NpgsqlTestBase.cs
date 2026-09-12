namespace NSLabs.EFCore.Extensions.Tests.Integration.Npgsql;

[Collection("npgsql")]
public abstract class NpgsqlTestBase(NpgsqlFixture fixture)
{
    protected NpgsqlFixture Fixture { get; } = fixture;

    protected void RequireDatabase()
    {
        if (Fixture.UnavailableReason is { } reason)
        {
            Assert.Skip(reason);
        }
    }
}
