using System.Linq.Expressions;

namespace EF.Core.Extensions;

public interface IBulkBatch
{
    IBulkBatch Update<TEntity>(Action<UpdateOperationBuilder<TEntity>> configure) where TEntity : class;

    IBulkBatch Update<TEntity>(IEnumerable<TEntity> rows) where TEntity : class;

    IBulkBatch Update<TEntity>(IEnumerable<TEntity> rows, Expression<Func<TEntity, TEntity, bool>> match) where TEntity : class;

    IBulkBatch Upsert<TEntity>(Action<UpsertOperationBuilder<TEntity>> configure) where TEntity : class;

    IBulkBatch Delete<TEntity>(Action<DeleteOperationBuilder<TEntity>> configure) where TEntity : class;

    Task<BulkExecuteResult> ExecuteAsync(CancellationToken cancellationToken = default);

    Task<BulkExecuteResult> ExecuteAsync(BulkExecuteOptions options, CancellationToken cancellationToken = default);
}
