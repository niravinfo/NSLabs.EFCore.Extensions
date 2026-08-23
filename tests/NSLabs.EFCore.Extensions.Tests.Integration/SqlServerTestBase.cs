using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Sdk;

namespace NSLabs.EFCore.Extensions.Tests.Integration;

[Collection("sqlserver")]
public abstract class SqlServerTestBase(SqlServerFixture fixture)
{
    protected SqlServerFixture Fixture { get; } = fixture;

    protected void RequireDatabase()
    {
        if (Fixture.UnavailableReason is { } reason)
        {
            Skip.If(true, reason);
        }
    }
}
