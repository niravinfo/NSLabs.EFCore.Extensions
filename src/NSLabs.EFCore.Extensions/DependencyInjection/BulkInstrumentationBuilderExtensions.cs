using Microsoft.EntityFrameworkCore.Infrastructure;
using NSLabs.EFCore.Extensions;
using NSLabs.EFCore.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore;

public static class BulkInstrumentationBuilderExtensions
{
    public static DbContextOptionsBuilder UseBulkInstrumentation(
        this DbContextOptionsBuilder builder,
        Action<BulkInstrumentationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = GetCurrentOptions(builder);
        configure(options);
        options.Validate();
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(
            new BulkInstrumentationOptionsExtension(options));

        return builder;
    }

    public static DbContextOptionsBuilder UseBulkInstrumentation(
        this DbContextOptionsBuilder builder,
        BulkInstrumentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        // Copy-in via the extension constructor; validate the caller's values before storing.
        options.Validate();
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(
            new BulkInstrumentationOptionsExtension(options));

        return builder;
    }

    public static DbContextOptionsBuilder<TContext> UseBulkInstrumentation<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        Action<BulkInstrumentationOptions> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        UseBulkInstrumentation((DbContextOptionsBuilder)builder, configure);
        return builder;
    }

    public static DbContextOptionsBuilder<TContext> UseBulkInstrumentation<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        BulkInstrumentationOptions options)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        UseBulkInstrumentation((DbContextOptionsBuilder)builder, options);
        return builder;
    }

    private static BulkInstrumentationOptions GetCurrentOptions(DbContextOptionsBuilder builder)
    {
        // Additive across repeated calls: start from the previous snapshot so a second
        // UseBulkInstrumentation call only overrides what its action touches.
        var existing = builder.Options.FindExtension<BulkInstrumentationOptionsExtension>();
        return existing is not null ? existing.Options : new BulkInstrumentationOptions();
    }
}
