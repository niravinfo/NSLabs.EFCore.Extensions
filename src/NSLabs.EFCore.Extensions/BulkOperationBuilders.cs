using System.Linq.Expressions;

namespace NSLabs.EFCore.Extensions;

public sealed class UpdateOperationBuilder<TEntity> where TEntity : class
{
    internal Expression<Func<TEntity, bool>>? Predicate { get; private set; }

    internal List<(LambdaExpression Selector, object? Value, LambdaExpression? ValueExpression)> Sets { get; } = [];

    public UpdateOperationBuilder<TEntity> Where(Expression<Func<TEntity, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Predicate = predicate;
        return this;
    }

    public UpdateOperationBuilder<TEntity> Set<TValue>(Expression<Func<TEntity, TValue>> selector, TValue value)
    {
        ArgumentNullException.ThrowIfNull(selector);
        Sets.Add((selector, value, null));
        return this;
    }

    public UpdateOperationBuilder<TEntity> Set<TValue>(Expression<Func<TEntity, TValue>> selector, Expression<Func<TEntity, TValue>> valueExpression)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(valueExpression);
        Sets.Add((selector, null, valueExpression));
        return this;
    }

    public UpdateOperationBuilder<TEntity> SetProperty<TValue>(Expression<Func<TEntity, TValue>> selector, Expression<Func<TEntity, TValue>> valueExpression)
        => Set(selector, valueExpression);
}

public sealed class DeleteOperationBuilder<TEntity> where TEntity : class
{
    internal Expression<Func<TEntity, bool>>? Predicate { get; private set; }

    public DeleteOperationBuilder<TEntity> Where(Expression<Func<TEntity, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Predicate = predicate;
        return this;
    }
}

public sealed class UpsertOperationBuilder<TEntity> where TEntity : class
{
    internal LambdaExpression? ConflictTarget { get; private set; }

    internal Expression<Func<TEntity, bool>>? Guard { get; private set; }

    internal List<(LambdaExpression Selector, object? Value, LambdaExpression? ValueExpression)> Sets { get; } = [];

    internal List<TEntity> Rows { get; } = [];

    public UpsertOperationBuilder<TEntity> MatchOn<TConflict>(Expression<Func<TEntity, TConflict>> conflictTarget)
    {
        ArgumentNullException.ThrowIfNull(conflictTarget);
        ConflictTarget = conflictTarget;
        return this;
    }

    public UpsertOperationBuilder<TEntity> UpdateWhen(Expression<Func<TEntity, bool>> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        Guard = guard;
        return this;
    }

    public UpsertOperationBuilder<TEntity> Update<TValue>(Expression<Func<TEntity, TValue>> selector, TValue value)
    {
        ArgumentNullException.ThrowIfNull(selector);
        Sets.Add((selector, value, null));
        return this;
    }

    public UpsertOperationBuilder<TEntity> Update<TValue>(Expression<Func<TEntity, TValue>> selector, Expression<Func<TEntity, TValue>> valueExpression)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(valueExpression);
        Sets.Add((selector, null, valueExpression));
        return this;
    }

    public UpsertOperationBuilder<TEntity> SetProperty<TValue>(Expression<Func<TEntity, TValue>> selector, Expression<Func<TEntity, TValue>> valueExpression)
        => Update(selector, valueExpression);

    public UpsertOperationBuilder<TEntity> Insert(TEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Rows.Add(row);
        return this;
    }

    public UpsertOperationBuilder<TEntity> Insert(IEnumerable<TEntity> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Rows.AddRange(rows);
        return this;
    }
}
