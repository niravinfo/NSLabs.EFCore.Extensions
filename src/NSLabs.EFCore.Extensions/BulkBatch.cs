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

        ValidateUniqueUpsertKeys(_operations);

        var providerName = _context.Database.ProviderName;
        var provider = BulkProviderRegistry.Resolve(providerName);
        if (provider is null)
        {
            throw new NotSupportedException(
                $"Provider '{providerName}' is not supported. Ensure the matching NSLabs.EFCore.Extensions.* provider package is referenced (e.g. NSLabs.EFCore.Extensions.SqlServer for SQL Server).");
        }

        var chunks = provider.Generate(_operations, options.MaxParametersPerCommand);
        var counts = await provider.ExecuteAsync(_context, chunks, _operations, options, cancellationToken).ConfigureAwait(false);

        var operationResults = _operations
            .Select(op => new OperationResult(op.EntityType.DisplayName(), counts.GetValueOrDefault(op.GlobalIndex)))
            .ToArray();

        return new BulkExecuteResult
        {
            TotalRowsAffected = counts.Values.Sum(),
            Operations = operationResults
        };
    }

    internal static void ValidateUniqueUpsertKeys(IReadOnlyList<BoundOperation> operations)
    {
        var buckets = new Dictionary<(string Table, string Shape), Dictionary<UpsertKey, (int OpIndex, int RowIndex)>>();

        foreach (var operation in operations)
        {
            if (operation.Kind != BulkOperationKind.Upsert || operation.UpsertSpec is not { } spec)
            {
                continue;
            }

            var table = operation.EntityType.GetTableName() ?? operation.EntityType.DisplayName();
            var shape = string.Join("|", spec.ConflictProperties.Select(p => p.Name));
            var bucket = buckets.TryGetValue((table, shape), out var existing)
                ? existing
                : buckets[(table, shape)] = [];

            for (var rowIndex = 0; rowIndex < spec.Rows.Count; rowIndex++)
            {
                var key = new UpsertKey(spec.Rows[rowIndex].KeyValues);                if (bucket.TryGetValue(key, out var collision))
                {
                    throw new InvalidOperationException(
                        $"Duplicate upsert match-key on '{table}' between operation #{collision.OpIndex} (row {collision.RowIndex}) " +
                        $"and operation #{operation.GlobalIndex} (row {rowIndex}). SQL Server MERGE cannot affect the same row twice in one batch.");
                }

                bucket[key] = (operation.GlobalIndex, rowIndex);
            }
        }
    }

    private sealed class UpsertKey(IReadOnlyList<object?> values)
    {
        private readonly IReadOnlyList<object?> _values = values;

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                foreach (var value in _values)
                {
                    hash = hash * 31 + (value?.GetHashCode() ?? 0);
                }

                return hash;
            }
        }

        public override bool Equals(object? obj)
            => obj is UpsertKey other
               && _values.Count == other._values.Count
               && _values.SequenceEqual(other._values, EqualityComparer<object?>.Default);
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

        var assignedUpdateColumns = new HashSet<IProperty>();
        foreach (var (selector, value, valueExpression) in builder.Sets)
        {
            var property = ModelBinder.ResolveSelector(selector, entityType);
            if (!assignedUpdateColumns.Add(property))
            {
                throw new InvalidOperationException(
                    $"Update operation #{operation.GlobalIndex} on '{entityType.DisplayName()}' assigns '{property.Name}' more than once; " +
                    "combine the calls into a single Set(...) per column.");
            }

            if (valueExpression is not null)
            {
                ModelBinder.EnsureWritable(property);
                var sqlNode = SetExpressionTranslator.Translate(valueExpression, entityType, valueExpression.Parameters[0]);
                ValidateComputedAssignmentType(property, valueExpression);
                NormalizeComputedParameters(sqlNode);
                operation.Assignments.Add(new BoundAssignment { Property = property, ValueExpression = sqlNode });
            }
            else
            {
                operation.Assignments.Add(ModelBinder.CreateAssignment(property, value));
            }
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

        var conflictProperties = builder.ConflictTarget is not null
            ? ResolveConflictProperties(builder.ConflictTarget, entityType)
            : entityType.FindPrimaryKey()?.Properties.ToList()
              ?? throw new InvalidOperationException(
                  $"Upsert operation #{operation.GlobalIndex} on '{entityType.DisplayName()}' requires On(...) because the entity has no primary key.");

        var discriminator = entityType.FindDiscriminatorProperty();
        object? discriminatorValue = null;
        if (discriminator is not null)
        {
            discriminatorValue = entityType.GetDiscriminatorValue()
                ?? throw new InvalidOperationException(
                    $"Entity '{entityType.DisplayName()}' has a discriminator but no concrete discriminator value; upsert requires a leaf entity type.");
        }

        var insertColumns = new List<IProperty>(conflictProperties);
        foreach (var property in entityType.GetProperties())
        {
            // Insert columns allow non-store-generated primary keys (natural-key upserts);
            // store-generated columns (identity/computed) are always database-managed.
            if (!ModelBinder.IsInsertBindable(property) || insertColumns.Contains(property))
            {
                continue;
            }

            insertColumns.Add(property);
        }

        if (discriminator is not null && !insertColumns.Contains(discriminator))
        {
            insertColumns.Add(discriminator);
        }

        var assignedUpsertColumns = new HashSet<IProperty>();
        foreach (var (selector, value, valueExpression) in builder.Sets)
        {
            var property = ModelBinder.ResolveSelector(selector, entityType);
            if (conflictProperties.Contains(property))
            {
                throw new InvalidOperationException(
                    $"Upsert operation #{operation.GlobalIndex} on '{entityType.DisplayName()}': Set(...) cannot target conflict column '{property.Name}'.");
            }

            if (!assignedUpsertColumns.Add(property))
            {
                throw new InvalidOperationException(
                    $"Upsert operation #{operation.GlobalIndex} on '{entityType.DisplayName()}' assigns '{property.Name}' more than once; " +
                    "combine the calls into a single Set(...) per column.");
            }

            if (valueExpression is not null)
            {
                ModelBinder.EnsureWritable(property);
                var sqlNode = SetExpressionTranslator.Translate(valueExpression, entityType, valueExpression.Parameters[0]);
                ValidateComputedAssignmentType(property, valueExpression);
                NormalizeComputedParameters(sqlNode);
                operation.Assignments.Add(new BoundAssignment { Property = property, ValueExpression = sqlNode });
            }
            else
            {
                operation.Assignments.Add(ModelBinder.CreateAssignment(property, value));
            }
        }

        // Explicit Set(...) values form the matched-update payload; otherwise the full row
        // (minus conflict and generated columns) is written back, mirroring entity-style updates.
        var updateColumns = builder.Sets.Count > 0
            ? []
            : insertColumns.Where(column => ModelBinder.IsBindableScalar(column) && !conflictProperties.Contains(column)).ToList();

        var hasMatchedUpdatePayload = builder.Sets.Count > 0 || updateColumns.Count > 0;

        var spec = new BoundUpsertSpec
        {
            ConflictProperties = conflictProperties,
            InsertColumns = insertColumns,
            UpdateColumns = updateColumns
        };

        if (builder.Guard is not null)
        {
            if (!hasMatchedUpdatePayload)
            {
                throw new InvalidOperationException(
                    $"Upsert operation #{operation.GlobalIndex} on '{entityType.DisplayName()}': WhenMatched(...) guards the matched update, but there is nothing to update; add Set(...) or non-key columns.");
            }

            spec.Guard = LinqPredicateTranslator.Translate(builder.Guard, entityType, builder.Guard.Parameters[0]);
        }

        foreach (var row in builder.Rows)
        {
            var insertValues = new List<BoundAssignment>(insertColumns.Count);
            foreach (var property in insertColumns)
            {
                var raw = ReferenceEquals(property, discriminator)
                    ? discriminatorValue
                    : ModelBinder.ReadMemberValue(property, row!);

                insertValues.Add(new BoundAssignment
                {
                    Property = property,
                    Value = ModelBinder.ConvertToProvider(property, raw)
                });
            }

            var keyValues = new object?[conflictProperties.Count];
            for (var i = 0; i < conflictProperties.Count; i++)
            {
                keyValues[i] = insertValues.First(assignment => ReferenceEquals(assignment.Property, conflictProperties[i])).Value;
            }

            spec.Rows.Add(new BoundUpsertRow { InsertValues = insertValues, KeyValues = keyValues });
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

    private static void ValidateComputedAssignmentType(IProperty property, LambdaExpression valueExpression)
    {
        var targetType = property.ClrType;
        var exprType = valueExpression.ReturnType;

        // Allow exact match, nullable unwrapping, and numeric widening.
        if (targetType == exprType || targetType.IsAssignableFrom(exprType))
        {
            return;
        }

        var underlyingTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
        var underlyingExpr = Nullable.GetUnderlyingType(exprType) ?? exprType;

        if (underlyingTarget == underlyingExpr || underlyingTarget.IsAssignableFrom(underlyingExpr))
        {
            return;
        }

        // Allow implicit numeric conversions (int -> long, int -> decimal, etc.)
        if (IsNumericType(underlyingTarget) && IsNumericType(underlyingExpr))
        {
            return;
        }

        // Allow enum <-> underlying numeric
        if (underlyingTarget.IsEnum && IsNumericType(underlyingExpr))
        {
            return;
        }

        if (underlyingExpr.IsEnum && IsNumericType(underlyingTarget))
        {
            return;
        }

        if (underlyingTarget.IsEnum && underlyingExpr.IsEnum && underlyingTarget == underlyingExpr)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Computed SET expression type '{exprType.Name}' is not assignable to property '{property.Name}' of type '{targetType.Name}'.");
    }

    private static bool IsNumericType(Type type)
        => Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
            or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
            or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    private static void NormalizeComputedParameters(SqlNode node)
    {
        switch (node)
        {
            case SqlParameterNode:
                break;
            case SqlBinaryNode binary:
                NormalizeComputedParameters(binary.Left);
                NormalizeComputedParameters(binary.Right);
                break;
            case SqlUnaryNode unary:
                NormalizeComputedParameters(unary.Inner);
                break;
            case SqlNotNode not:
                NormalizeComputedParameters(not.Inner);
                break;
            case SqlConditionalNode cond:
                NormalizeComputedParameters(cond.Test);
                NormalizeComputedParameters(cond.IfTrue);
                NormalizeComputedParameters(cond.IfFalse);
                break;
            case SqlCoalesceNode co:
                NormalizeComputedParameters(co.Left);
                NormalizeComputedParameters(co.Right);
                break;
            case SqlMethodCallNode method:
                foreach (var arg in method.Args)
                    NormalizeComputedParameters(arg);
                break;
            case SqlColumnNode:
            case SqlBooleanNode:
            case SqlNullCheckNode:
                break;
        }
    }
}
