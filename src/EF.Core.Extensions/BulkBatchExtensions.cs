using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EF.Core.Extensions;

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
        => context.BulkExecuteAsync(build, new BulkExecuteOptions(), cancellationToken);

    public static async Task<BulkExecuteResult> BulkExecuteAsync(
        this DbContext context,
        Action<IBulkBatch> build,
        BulkExecuteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(build);

        var batch = new BulkBatch(context);
        build(batch);
        return await batch.ExecuteAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public static Task<BulkExecuteResult> BulkUpdateAsync<TEntity>(
        this DbSet<TEntity> set,
        Action<TableUpdateBuilder<TEntity>> configure,
        CancellationToken cancellationToken = default) where TEntity : class
        => set.BulkUpdateAsync(configure, new BulkExecuteOptions(), cancellationToken);

    public static Task<BulkExecuteResult> BulkUpdateAsync<TEntity>(
        this DbSet<TEntity> set,
        Action<TableUpdateBuilder<TEntity>> configure,
        BulkExecuteOptions options,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        var batch = new BulkBatch(GetContext(set));
        configure(new TableUpdateBuilder<TEntity>(batch));
        return batch.ExecuteAsync(options, cancellationToken);
    }

    public static Task<BulkExecuteResult> BulkUpsertAsync<TEntity>(
        this DbSet<TEntity> set,
        Action<TableUpsertBuilder<TEntity>> configure,
        CancellationToken cancellationToken = default) where TEntity : class
        => set.BulkUpsertAsync(configure, new BulkExecuteOptions(), cancellationToken);

    public static Task<BulkExecuteResult> BulkUpsertAsync<TEntity>(
        this DbSet<TEntity> set,
        Action<TableUpsertBuilder<TEntity>> configure,
        BulkExecuteOptions options,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        var batch = new BulkBatch(GetContext(set));
        configure(new TableUpsertBuilder<TEntity>(batch));
        return batch.ExecuteAsync(options, cancellationToken);
    }

    private static DbContext GetContext<TEntity>(DbSet<TEntity> set) where TEntity : class
        => ((IInfrastructure<IServiceProvider>)set).Instance.GetRequiredService<ICurrentDbContext>().Context;
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
