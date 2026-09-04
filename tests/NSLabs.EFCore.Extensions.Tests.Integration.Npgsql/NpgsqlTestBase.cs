using Xunit.Sdk;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Npgsql;

[Collection("npgsql")]
public abstract class NpgsqlTestBase(NpgsqlFixture fixture)
{
    protected NpgsqlFixture Fixture { get; } = fixture;

    protected void RequireDatabase()
    {
        if (Fixture.UnavailableReason is { } reason)
        {
            Skip.If(true, reason);
        }
    }
}
