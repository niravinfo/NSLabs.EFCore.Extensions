using Microsoft.EntityFrameworkCore.Infrastructure;
using NSLabs.EFCore.Extensions;
using NSLabs.EFCore.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore;

public static class BulkExecuteBuilderExtensions
{
    public static DbContextOptionsBuilder UseBulkExecute(
        this DbContextOptionsBuilder builder,
        Action<BulkExecuteOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = GetCurrentOptions(builder);
        configure(options);
        options.Validate();
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(
            new BulkExecuteOptionsExtension(options));

        return builder;
    }

    public static DbContextOptionsBuilder UseBulkExecute(
        this DbContextOptionsBuilder builder,
        BulkExecuteOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        // Copy-in via the extension constructor; validate the caller's values before storing.
        options.Validate();
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(
            new BulkExecuteOptionsExtension(options));

        return builder;
    }

    public static DbContextOptionsBuilder<TContext> UseBulkExecute<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        Action<BulkExecuteOptions> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        UseBulkExecute((DbContextOptionsBuilder)builder, configure);
        return builder;
    }

    public static DbContextOptionsBuilder<TContext> UseBulkExecute<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        BulkExecuteOptions options)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        UseBulkExecute((DbContextOptionsBuilder)builder, options);
        return builder;
    }

    private static BulkExecuteOptions GetCurrentOptions(DbContextOptionsBuilder builder)
    {
        // Additive across repeated calls: start from the previous snapshot so a second
        // UseBulkExecute call only overrides what its action touches.
        var existing = builder.Options.FindExtension<BulkExecuteOptionsExtension>();
        return existing is not null ? existing.Options : new BulkExecuteOptions();
    }
}
