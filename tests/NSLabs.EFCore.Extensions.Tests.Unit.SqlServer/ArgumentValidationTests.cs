using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

/// <summary>
/// Argument-validation contracts on the public entry points (<c>BulkBatchExtensions</c>).
///
/// These exercise the provider-agnostic core assembly only, so they live in this project
/// next to the other core-level validation tests rather than being duplicated across all
/// three provider test projects.
///
/// Note on argument positions: these are extension methods, so the receiver supplies the
/// <c>set</c> parameter. To pass a null <c>set</c> the receiver itself must be null, which
/// is why the null-<c>set</c> cases use a <c>DbSet&lt;T&gt;</c> local rather than
/// <c>context.Items</c>.
/// </summary>
public class ArgumentValidationTests
{
    /// <summary>
    /// Thrown by test callbacks that must never run. If argument validation is ordered
    /// correctly the callback is never invoked, so this never surfaces; if it does surface,
    /// the test failed for the right reason (user code ran before validation).
    /// </summary>
    private sealed class CallbackMustNotRunException()
        : Exception("The user callback ran before arguments were validated.");

    private static TestDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer("Server=tcp:localhost,1433;Database=BulkExtensionsTest;User Id=test;Password=test;TrustServerCertificate=True;")
            .Options;
        return new Harness.SqlServerUnitTestDbContext(opts);
    }

    // In the null-'set' cases 'configure' is null too, on purpose: they also pin the
    // *order* of validation, so 'set' must be reported even though 'configure' is
    // also invalid.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulkUpdateAsync_rejects_null_set_naming_set(bool withOptions)
    {
        using var context = CreateContext();
        DbSet<Item> nullSet = null!;

        var ex = Assert.Throws<ArgumentNullException>(() =>
        {
            if (withOptions)
            {
                _ = nullSet.BulkUpdateAsync(null!, new BulkExecuteOptions(), TestContext.Current.CancellationToken);
            }
            else
            {
                _ = nullSet.BulkUpdateAsync(null!, TestContext.Current.CancellationToken);
            }
        });

        Assert.Equal("set", ex.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulkUpdateAsync_rejects_null_configure_naming_configure(bool withOptions)
    {
        using var context = CreateContext();

        var ex = Assert.Throws<ArgumentNullException>(() =>
        {
            if (withOptions)
            {
                _ = context.Items.BulkUpdateAsync(null!, new BulkExecuteOptions(), TestContext.Current.CancellationToken);
            }
            else
            {
                _ = context.Items.BulkUpdateAsync(null!, TestContext.Current.CancellationToken);
            }
        });

        Assert.Equal("configure", ex.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulkUpsertAsync_rejects_null_set_naming_set(bool withOptions)
    {
        using var context = CreateContext();
        DbSet<Item> nullSet = null!;

        var ex = Assert.Throws<ArgumentNullException>(() =>
        {
            if (withOptions)
            {
                _ = nullSet.BulkUpsertAsync(null!, new BulkExecuteOptions(), TestContext.Current.CancellationToken);
            }
            else
            {
                _ = nullSet.BulkUpsertAsync(null!, TestContext.Current.CancellationToken);
            }
        });

        Assert.Equal("set", ex.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulkUpsertAsync_rejects_null_configure_naming_configure(bool withOptions)
    {
        using var context = CreateContext();

        var ex = Assert.Throws<ArgumentNullException>(() =>
        {
            if (withOptions)
            {
                _ = context.Items.BulkUpsertAsync(null!, new BulkExecuteOptions(), TestContext.Current.CancellationToken);
            }
            else
            {
                _ = context.Items.BulkUpsertAsync(null!, TestContext.Current.CancellationToken);
            }
        });

        Assert.Equal("configure", ex.ParamName);
    }

    [Fact]
    public void BulkExecuteAsync_rejects_null_options_before_running_the_callback()
    {
        using var context = CreateContext();
        var callbackRan = false;

        var ex = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = context.BulkExecuteAsync(
                _ =>
                {
                    callbackRan = true;
                    throw new CallbackMustNotRunException();
                },
                (BulkExecuteOptions)null!,
                TestContext.Current.CancellationToken);
        });

        Assert.Equal("options", ex.ParamName);
        Assert.False(callbackRan);
    }

    [Fact]
    public void BulkExecuteAsync_rejects_invalid_options_before_running_the_callback()
    {
        using var context = CreateContext();
        var callbackRan = false;
        var options = new BulkExecuteOptions { MaxParametersPerCommand = 0 };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = context.BulkExecuteAsync(
                _ =>
                {
                    callbackRan = true;
                    throw new CallbackMustNotRunException();
                },
                options,
                TestContext.Current.CancellationToken);
        });

        Assert.Equal(nameof(BulkExecuteOptions.MaxParametersPerCommand), ex.ParamName);
        Assert.False(callbackRan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DbSet_bulk_methods_reject_null_options_before_running_configure(bool upsert)
    {
        using var context = CreateContext();
        var configureRan = false;

        var ex = Assert.Throws<ArgumentNullException>(() =>
        {
            if (upsert)
            {
                _ = context.Items.BulkUpsertAsync(
                    _ =>
                    {
                        configureRan = true;
                        throw new CallbackMustNotRunException();
                    },
                    (BulkExecuteOptions)null!,
                    TestContext.Current.CancellationToken);
            }
            else
            {
                _ = context.Items.BulkUpdateAsync(
                    _ =>
                    {
                        configureRan = true;
                        throw new CallbackMustNotRunException();
                    },
                    (BulkExecuteOptions)null!,
                    TestContext.Current.CancellationToken);
            }
        });

        Assert.Equal("options", ex.ParamName);
        Assert.False(configureRan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DbSet_bulk_methods_reject_invalid_options_before_running_configure(bool upsert)
    {
        using var context = CreateContext();
        var configureRan = false;
        var options = new BulkExecuteOptions { CommandTimeout = -1 };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            if (upsert)
            {
                _ = context.Items.BulkUpsertAsync(
                    _ =>
                    {
                        configureRan = true;
                        throw new CallbackMustNotRunException();
                    },
                    options,
                    TestContext.Current.CancellationToken);
            }
            else
            {
                _ = context.Items.BulkUpdateAsync(
                    _ =>
                    {
                        configureRan = true;
                        throw new CallbackMustNotRunException();
                    },
                    options,
                    TestContext.Current.CancellationToken);
            }
        });

        Assert.Equal(nameof(BulkExecuteOptions.CommandTimeout), ex.ParamName);
        Assert.False(configureRan);
    }

    [Fact]
    public async Task BulkExecuteAsync_still_runs_the_callback_when_options_are_valid()
    {
        using var context = CreateContext();
        var callbackRan = false;

        // Guards against "fixing" the ordering by validating so eagerly that the callback
        // never runs. An empty batch short-circuits before any database access.
        var result = await context.BulkExecuteAsync(
            _ => { callbackRan = true; },
            new BulkExecuteOptions(),
            TestContext.Current.CancellationToken);

        Assert.True(callbackRan);
        Assert.Equal(0, result.TotalRowsAffected);
    }
}
