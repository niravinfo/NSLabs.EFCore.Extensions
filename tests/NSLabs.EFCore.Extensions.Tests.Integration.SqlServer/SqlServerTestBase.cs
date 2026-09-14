using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.SqlServer;

[Collection("sqlserver")]
public abstract class SqlServerTestBase(SqlServerFixture fixture)
{
    protected SqlServerFixture Fixture { get; } = fixture;

    protected void RequireDatabase()
    {
        if (Fixture.UnavailableReason is { } reason)
        {
            Assert.Skip(reason);
        }
    }
}
