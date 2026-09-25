using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace NSLabs.EFCore.Extensions;

public static class BulkBatchExtensions
{
    public static IBulkBatch CreateBulkBatch(this DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new BulkBatch(context);
    }

    public static Task<BulkExecuteResult> BulkExecuteAsync(
        this DbContext context,
        Action<IBulkBatch> build,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(build);

        var batch = new BulkBatch(context);
        build(batch);
        return batch.ExecuteAsync(cancellationToken);
    }

    /// <remarks>
    /// <paramref name="options"/> fully replaces any <c>UseBulkExecute</c> configuration for
    /// this call and is used directly without a defensive copy: do not mutate it while the
    /// returned task is in flight. Sharing a read-only instance across calls and threads is safe.
    /// </remarks>
    public static async Task<BulkExecuteResult> BulkExecuteAsync(
        this DbContext context,
        Action<IBulkBatch> build,
        BulkExecuteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(options);

        // P7: validate options before the user callback runs — argument validation must
        // never be observed after user code has already executed (.NET ordering convention).
        options.Validate();

        var batch = new BulkBatch(context);
        build(batch);
        return await batch.ExecuteAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public static Task<BulkExecuteResult> BulkUpdateAsync<TEntity>(
        this DbSet<TEntity> set,
        Action<TableUpdateBuilder<TEntity>> configure,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(configure);
        var batch = new BulkBatch(GetContext(set));
        configure(new TableUpdateBuilder<TEntity>(batch));
        return batch.ExecuteAsync(cancellationToken);
    }

    /// <remarks>
    /// <paramref name="options"/> fully replaces any <c>UseBulkExecute</c> configuration for
    /// this call and is used directly without a defensive copy: do not mutate it while the
    /// returned task is in flight. Sharing a read-only instance across calls and threads is safe.
    /// </remarks>
    public static Task<BulkExecuteResult> BulkUpdateAsync<TEntity>(
        this DbSet<TEntity> set,
        Action<TableUpdateBuilder<TEntity>> configure,
        BulkExecuteOptions options,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var batch = new BulkBatch(GetContext(set));
        configure(new TableUpdateBuilder<TEntity>(batch));
        return batch.ExecuteAsync(options, cancellationToken);
    }

    public static Task<BulkExecuteResult> BulkUpsertAsync<TEntity>(
        this DbSet<TEntity> set,
        Action<TableUpsertBuilder<TEntity>> configure,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(configure);
        var batch = new BulkBatch(GetContext(set));
        configure(new TableUpsertBuilder<TEntity>(batch));
        return batch.ExecuteAsync(cancellationToken);
    }

    /// <remarks>
    /// <paramref name="options"/> fully replaces any <c>UseBulkExecute</c> configuration for
    /// this call and is used directly without a defensive copy: do not mutate it while the
    /// returned task is in flight. Sharing a read-only instance across calls and threads is safe.
    /// </remarks>
    public static Task<BulkExecuteResult> BulkUpsertAsync<TEntity>(
        this DbSet<TEntity> set,
        Action<TableUpsertBuilder<TEntity>> configure,
        BulkExecuteOptions options,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var batch = new BulkBatch(GetContext(set));
        configure(new TableUpsertBuilder<TEntity>(batch));
        return batch.ExecuteAsync(options, cancellationToken);
    }

    private static DbContext GetContext<TEntity>(DbSet<TEntity> set) where TEntity : class
        => ((IInfrastructure<IServiceProvider>)set ?? throw new ArgumentNullException(nameof(set))).Instance.GetRequiredService<ICurrentDbContext>().Context;
}

public sealed class TableUpdateBuilder<TEntity> where TEntity : class
{
    private readonly IBulkBatch _batch;

    internal TableUpdateBuilder(IBulkBatch batch) => _batch = batch;

    public TableUpdateBuilder<TEntity> Add(Action<UpdateOperationBuilder<TEntity>> configure)
    {
        _batch.Update(configure);
        return this;
    }

    public TableUpdateBuilder<TEntity> Add(IEnumerable<TEntity> rows)
    {
        _batch.Update(rows);
        return this;
    }

    public TableUpdateBuilder<TEntity> Add(IEnumerable<TEntity> rows, Expression<Func<TEntity, TEntity, bool>> match)
    {
        _batch.Update(rows, match);
        return this;
    }
}

public sealed class TableUpsertBuilder<TEntity> where TEntity : class
{
    private readonly IBulkBatch _batch;

    internal TableUpsertBuilder(IBulkBatch batch) => _batch = batch;

    public TableUpsertBuilder<TEntity> Add(Action<UpsertOperationBuilder<TEntity>> configure)
    {
        _batch.Upsert(configure);
        return this;
    }
}
