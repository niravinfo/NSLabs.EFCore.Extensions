using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions;

public sealed class BulkBatch(DbContext context) : IBulkBatch
{
    private readonly DbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    private readonly List<BoundOperation> _operations = [];

    internal IReadOnlyList<BoundOperation> Operations => _operations;

    public IBulkBatch Update<TEntity>(Action<UpdateOperationBuilder<TEntity>> configure) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new UpdateOperationBuilder<TEntity>();
        configure(builder);
        _operations.Add(BindUpdate(builder));
        return this;
    }

    public IBulkBatch Update<TEntity>(IEnumerable<TEntity> rows) where TEntity : class
        => BindEntityRows(rows, match: null, update: true);

    public IBulkBatch Update<TEntity>(IEnumerable<TEntity> rows, Expression<Func<TEntity, TEntity, bool>> match) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(match);
        return BindEntityRows(rows, match, update: true);
    }

    public IBulkBatch Upsert<TEntity>(Action<UpsertOperationBuilder<TEntity>> configure) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new UpsertOperationBuilder<TEntity>();
        configure(builder);
        _operations.Add(BindUpsert(builder));
        return this;
    }

    public IBulkBatch Delete<TEntity>(Action<DeleteOperationBuilder<TEntity>> configure) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new DeleteOperationBuilder<TEntity>();
        configure(builder);
        _operations.Add(BindDelete(builder));
        return this;
    }

    public Task<BulkExecuteResult> ExecuteAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(new BulkExecuteOptions(), cancellationToken);

    public async Task<BulkExecuteResult> ExecuteAsync(BulkExecuteOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_operations.Count == 0)
        {
            return BulkExecuteResult.Empty;
        }

        var providerName = _context.Database.ProviderName;
        if (!string.Equals(providerName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Provider '{providerName}' is not supported yet. SQL Server is implemented; PostgreSQL, MySQL and SQLite are planned milestones.");
        }

        var chunks = SqlServerSqlGenerator.Generate(_operations, options.MaxParametersPerCommand);
        var counts = await SqlServerExecutor.ExecuteAsync(_context, chunks, options, cancellationToken).ConfigureAwait(false);

        if (options.ThrowIfZeroAffected)
        {
            var zero = _operations.FirstOrDefault(op => counts.TryGetValue(op.GlobalIndex, out var affected) && affected == 0);
            if (zero is not null)
            {
                throw new BulkZeroRowsAffectedException(zero.GlobalIndex, zero.EntityType.DisplayName());
            }
        }

        var operationResults = _operations
            .Select(op => new OperationResult(op.EntityType.DisplayName(), counts.GetValueOrDefault(op.GlobalIndex)))
            .ToArray();

        return new BulkExecuteResult
        {
            TotalRowsAffected = counts.Values.Sum(),
            Operations = operationResults
        };
    }

    private BoundOperation BindUpdate<TEntity>(UpdateOperationBuilder<TEntity> builder) where TEntity : class
    {
        var entityType = ModelBinder.ResolveEntityType<TEntity>(_context.Model);
        var operation = CreateOperation(BulkOperationKind.Update, entityType);

        if (builder.Predicate is null)
        {
            throw new InvalidOperationException($"Update operation #{operation.GlobalIndex} on '{entityType.DisplayName()}' requires Where(...).");
        }

        operation.PredicateParts.Add(LinqPredicateTranslator.Translate(builder.Predicate, entityType, builder.Predicate.Parameters[0]));

        foreach (var (selector, value) in builder.Sets)
        {
            var property = ModelBinder.ResolveSelector(selector, entityType);
            operation.Assignments.Add(ModelBinder.CreateAssignment(property, value));
        }

        if (operation.Assignments.Count == 0)
        {
            throw new InvalidOperationException($"Update operation #{operation.GlobalIndex} on '{entityType.DisplayName()}' has no Set(...) assignments.");
        }

        ModelBinder.AddDiscriminatorPart(operation);
        return operation;
    }

    private BoundOperation BindDelete<TEntity>(DeleteOperationBuilder<TEntity> builder) where TEntity : class
    {
        var entityType = ModelBinder.ResolveEntityType<TEntity>(_context.Model);
        var operation = CreateOperation(BulkOperationKind.Delete, entityType);

        if (builder.Predicate is null)
        {
            throw new InvalidOperationException($"Delete operation #{operation.GlobalIndex} on '{entityType.DisplayName()}' requires Where(...).");
        }

        operation.PredicateParts.Add(LinqPredicateTranslator.Translate(builder.Predicate, entityType, builder.Predicate.Parameters[0]));
        ModelBinder.AddDiscriminatorPart(operation);
        return operation;
    }

    private BoundOperation BindUpsert<TEntity>(UpsertOperationBuilder<TEntity> builder) where TEntity : class
    {
        var entityType = ModelBinder.ResolveEntityType<TEntity>(_context.Model);
        var operation = CreateOperation(BulkOperationKind.Upsert, entityType);

        var spec = new BoundUpsertSpec { RowCount = builder.Rows.Count };

        if (builder.ConflictTarget is not null)
        {
            spec.ConflictProperties = ResolveConflictProperties(builder.ConflictTarget, entityType);
        }

        if (builder.Guard is not null)
        {
            spec.Guard = LinqPredicateTranslator.Translate(builder.Guard, entityType, builder.Guard.Parameters[0]);
        }

        foreach (var (selector, _) in builder.Sets)
        {
            ModelBinder.EnsureWritable(ModelBinder.ResolveSelector(selector, entityType));
        }

        operation.UpsertSpec = spec;
        return operation;
    }

    private IBulkBatch BindEntityRows<TEntity>(IEnumerable<TEntity>? rows, Expression<Func<TEntity, TEntity, bool>>? match, bool update) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(rows);

        var materialized = rows as ICollection<TEntity> ?? rows.ToList();
        if (materialized.Count == 0)
        {
            return this;
        }

        var entityType = ModelBinder.ResolveEntityType<TEntity>(_context.Model);
        var primaryKey = entityType.FindPrimaryKey()
            ?? throw new InvalidOperationException($"Entity '{entityType.DisplayName()}' has no primary key; entity-style updates require one or an explicit match expression.");

        var bindableProperties = entityType.GetProperties().Where(ModelBinder.IsBindableScalar).ToArray();

        foreach (var row in materialized)
        {
            var operation = CreateOperation(update ? BulkOperationKind.Update : BulkOperationKind.Delete, entityType);

            if (match is null)
            {
                foreach (var keyProperty in primaryKey.Properties)
                {
                    operation.PredicateParts.Add(new SqlBinaryNode(
                        SqlBinaryOperator.Equal,
                        new SqlColumnNode(keyProperty),
                        new SqlParameterNode(ModelBinder.ConvertToProvider(keyProperty, ModelBinder.ReadMemberValue(keyProperty, row!)))));
                }
            }
            else
            {
                var rewrittenBody = ParameterReplacer.Replace(match.Body, match.Parameters[0], Expression.Constant(row, typeof(TEntity)));
                var rowPredicate = Expression.Lambda<Func<TEntity, bool>>(rewrittenBody, match.Parameters[1]);
                operation.PredicateParts.Add(LinqPredicateTranslator.Translate(rowPredicate, entityType, rowPredicate.Parameters[0]));
            }

            if (update)
            {
                foreach (var property in bindableProperties)
                {
                    operation.Assignments.Add(ModelBinder.CreateAssignment(property, ModelBinder.ReadMemberValue(property, row!)));
                }
            }

            ModelBinder.AddDiscriminatorPart(operation);
            _operations.Add(operation);
        }

        return this;
    }

    private List<IProperty> ResolveConflictProperties(LambdaExpression conflictTarget, IEntityType entityType)
    {
        var body = conflictTarget.Body;

        if (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            body = convert.Operand;
        }

        if (body is MemberExpression member && member.Expression is ParameterExpression)
        {
            return [ResolveProperty(member, entityType)];
        }

        if (body is NewExpression { Members: not null } newExpression)
        {
            return newExpression.Members
                .Select(memberInfo => entityType.FindProperty(memberInfo)
                    ?? entityType.FindProperty(memberInfo.Name)
                    ?? throw new InvalidOperationException($"Conflict target property '{memberInfo.Name}' is not part of the model for '{entityType.DisplayName()}'."))
                .ToList();
        }

        throw new NotSupportedException("Conflict target must be x => x.Prop or x => new { x.A, x.B }.");
    }

    private static IProperty ResolveProperty(MemberExpression member, IEntityType entityType)
        => entityType.FindProperty(member.Member)
           ?? entityType.FindProperty(member.Member.Name)
           ?? throw new InvalidOperationException($"Property '{member.Member.Name}' is not part of the model for '{entityType.DisplayName()}'.");

    private BoundOperation CreateOperation(BulkOperationKind kind, IEntityType entityType)
        => new()
        {
            GlobalIndex = _operations.Count,
            Kind = kind,
            EntityType = entityType
        };
}
