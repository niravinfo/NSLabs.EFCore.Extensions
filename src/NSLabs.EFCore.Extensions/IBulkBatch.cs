using System.Linq.Expressions;

namespace NSLabs.EFCore.Extensions;

public interface IBulkBatch
{
    IBulkBatch Update<TEntity>(Action<UpdateOperationBuilder<TEntity>> configure) where TEntity : class;

    IBulkBatch Update<TEntity>(IEnumerable<TEntity> rows) where TEntity : class;

    IBulkBatch Update<TEntity>(IEnumerable<TEntity> rows, Expression<Func<TEntity, TEntity, bool>> match) where TEntity : class;

    IBulkBatch Upsert<TEntity>(Action<UpsertOperationBuilder<TEntity>> configure) where TEntity : class;

    IBulkBatch Delete<TEntity>(Action<DeleteOperationBuilder<TEntity>> configure) where TEntity : class;

    Task<BulkExecuteResult> ExecuteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the batch with an explicit <see cref="BulkExecuteOptions"/> object, fully
    /// replacing any <c>UseBulkExecute</c> configuration for this call.
    /// </summary>
    /// <remarks>
    /// The instance is used directly without a defensive copy: do not mutate it while the
    /// returned task is in flight. Sharing a read-only instance across calls and threads is safe.
    /// </remarks>
    Task<BulkExecuteResult> ExecuteAsync(BulkExecuteOptions options, CancellationToken cancellationToken = default);
}
