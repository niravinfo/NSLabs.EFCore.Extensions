using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Shared;

public static class IntegrationTestHelper
{
    public static async Task ClearTableAsync<T>(DbContext context) where T : class
    {
        await context.Set<T>().ExecuteDeleteAsync();
    }
}
