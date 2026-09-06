using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NSLabs.EFCore.Extensions.DependencyInjection;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

public class BulkExecuteOptionsResolutionTests
{
    private const string FakeConnectionString =
        "Server=tcp:localhost,1433;Database=BulkExtensionsTest;User Id=test;Password=test;TrustServerCertificate=True;";

    private static TestDbContext CreateContext(Action<BulkExecuteOptions>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>().UseSqlServer(FakeConnectionString);
        if (configure is not null)
        {
            builder.UseBulkExecute(configure);
        }

        return new Harness.SqlServerUnitTestDbContext(builder.Options);
    }

    private static BulkExecuteOptionsExtension GetExtension(TestDbContext context)
        => context.GetService<IDbContextOptions>().FindExtension<BulkExecuteOptionsExtension>()
           ?? throw new InvalidOperationException("Expected BulkExecuteOptionsExtension to be present.");

    [Fact]
    public void Factory_defaults_are_unchanged()
    {
        var options = new BulkExecuteOptions();
        Assert.Equal(2000, options.MaxParametersPerCommand);
        Assert.False(options.ThrowIfZeroAffected);
        Assert.Null(options.CommandTimeout);
    }

    [Fact]
    public void Clone_is_independent_of_source()
    {
        var source = new BulkExecuteOptions { MaxParametersPerCommand = 4, ThrowIfZeroAffected = true, CommandTimeout = 30 };
        var clone = source.Clone();

        clone.MaxParametersPerCommand = 2000;
        clone.ThrowIfZeroAffected = false;
        clone.CommandTimeout = null;

        Assert.Equal(4, source.MaxParametersPerCommand);
        Assert.True(source.ThrowIfZeroAffected);
        Assert.Equal(30, source.CommandTimeout);
    }

    [Fact]
    public void CopyTo_copies_all_properties()
    {
        var source = new BulkExecuteOptions { MaxParametersPerCommand = 7, ThrowIfZeroAffected = true, CommandTimeout = 11 };
        var target = new BulkExecuteOptions();

        source.CopyTo(target);

        Assert.Equal(7, target.MaxParametersPerCommand);
        Assert.True(target.ThrowIfZeroAffected);
        Assert.Equal(11, target.CommandTimeout);
    }

    [Fact]
    public void UseBulkExecute_rejects_invalid_values()
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>().UseSqlServer(FakeConnectionString);

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseBulkExecute(o => o.MaxParametersPerCommand = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseBulkExecute(o => o.CommandTimeout = -1));
    }

    [Fact]
    public void UseBulkExecute_stores_a_copy_not_the_caller_instance()
    {
        BulkExecuteOptions? captured = null;
        using var context = CreateContext(o =>
        {
            o.ThrowIfZeroAffected = true;
            captured = o;
        });

        Assert.NotNull(captured);
        captured!.ThrowIfZeroAffected = false;
        captured.MaxParametersPerCommand = 3;

        var resolved = BulkBatch.ResolveEffective(context, explicitOptions: null);
        Assert.True(resolved.ThrowIfZeroAffected);
        Assert.Equal(2000, resolved.MaxParametersPerCommand);
    }

    [Fact]
    public void Options_getter_returns_a_copy()
    {
        using var context = CreateContext(o => o.ThrowIfZeroAffected = true);

        var exposed = GetExtension(context).Options;
        exposed.ThrowIfZeroAffected = false;

        Assert.True(BulkBatch.ResolveEffective(context, explicitOptions: null).ThrowIfZeroAffected);
    }

    [Fact]
    public void ResolveEffective_without_extension_returns_factory_defaults()
    {
        using var context = CreateContext();

        var resolved = BulkBatch.ResolveEffective(context, explicitOptions: null);

        Assert.Equal(2000, resolved.MaxParametersPerCommand);
        Assert.False(resolved.ThrowIfZeroAffected);
        Assert.Null(resolved.CommandTimeout);
    }

    [Fact]
    public void ResolveEffective_default_path_uses_DI_snapshot()
    {
        using var context = CreateContext(o =>
        {
            o.ThrowIfZeroAffected = true;
            o.CommandTimeout = 30;
            o.MaxParametersPerCommand = 9;
        });

        var resolved = BulkBatch.ResolveEffective(context, explicitOptions: null);

        Assert.True(resolved.ThrowIfZeroAffected);
        Assert.Equal(30, resolved.CommandTimeout);
        Assert.Equal(9, resolved.MaxParametersPerCommand);
    }

    [Fact]
    public void ResolveEffective_explicit_object_ignores_DI_snapshot()
    {
        using var context = CreateContext(o => o.ThrowIfZeroAffected = true);

        var resolved = BulkBatch.ResolveEffective(context, new BulkExecuteOptions { CommandTimeout = 60 });

        Assert.False(resolved.ThrowIfZeroAffected);
        Assert.Equal(60, resolved.CommandTimeout);
    }

    [Fact]
    public void ResolveEffective_clones_explicit_object_on_entry()
    {
        using var context = CreateContext();
        var explicitOptions = new BulkExecuteOptions { CommandTimeout = 60 };

        var resolved = BulkBatch.ResolveEffective(context, explicitOptions);
        explicitOptions.CommandTimeout = 1;

        Assert.Equal(60, resolved.CommandTimeout);
    }

    [Fact]
    public void Contexts_are_isolated_per_DbContext_options()
    {
        using var configured = CreateContext(o => o.ThrowIfZeroAffected = true);
        using var plain = CreateContext();

        Assert.True(BulkBatch.ResolveEffective(configured, explicitOptions: null).ThrowIfZeroAffected);
        Assert.False(BulkBatch.ResolveEffective(plain, explicitOptions: null).ThrowIfZeroAffected);
    }

    [Fact]
    public void Repeated_UseBulkExecute_calls_are_additive_last_wins()
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>().UseSqlServer(FakeConnectionString);
        builder.UseBulkExecute(o => o.ThrowIfZeroAffected = true);
        builder.UseBulkExecute(o => o.CommandTimeout = 45);
        using var context = new Harness.SqlServerUnitTestDbContext(builder.Options);

        var resolved = BulkBatch.ResolveEffective(context, explicitOptions: null);

        Assert.True(resolved.ThrowIfZeroAffected);
        Assert.Equal(45, resolved.CommandTimeout);
    }

    [Fact]
    public void UseBulkExecute_instance_overload_stores_a_copy()
    {
        var source = new BulkExecuteOptions { CommandTimeout = 12 };
        var builder = new DbContextOptionsBuilder<TestDbContext>().UseSqlServer(FakeConnectionString);
        builder.UseBulkExecute(source);
        source.CommandTimeout = 99;

        using var context = new Harness.SqlServerUnitTestDbContext(builder.Options);
        Assert.Equal(12, BulkBatch.ResolveEffective(context, explicitOptions: null).CommandTimeout);
    }
}
