using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class ModelBinder
{
    // SAFETY S4,S5,S6: caches are read-only after creation, never affect semantics
    private static readonly ConcurrentDictionary<IProperty, Func<object, object?>> _getterCache = new();

    private static readonly ConcurrentDictionary<IProperty, ValueConverter?> _converterCache = new();

    private static readonly ConcurrentDictionary<IEntityType, string> _tableNameCache = new();

    private static readonly ConcurrentDictionary<(IEntityType EntityType, IProperty Property), string> _columnNameCache = new();

    public static IEntityType ResolveEntityType<TEntity>(IModel model) where TEntity : class
        => model.FindEntityType(typeof(TEntity))
           ?? throw new InvalidOperationException(
               $"Entity '{typeof(TEntity).Name}' is not part of the DbContext model. Register it via DbSet<TEntity> or modelBuilder.Entity<{typeof(TEntity).Name}>().");

    public static string GetTableName(IEntityType entityType)
        => _tableNameCache.GetOrAdd(entityType, static et => et.GetTableName()
           ?? throw new InvalidOperationException($"Entity '{et.DisplayName()}' is not mapped to a relational table."));

    public static string GetColumnName(IProperty property, IEntityType entityType)
        => _columnNameCache.GetOrAdd((entityType, property), static key =>
        {
            var (et, prop) = key;
            var table = StoreObjectIdentifier.Create(et, StoreObjectType.Table)
                ?? throw new InvalidOperationException($"Cannot resolve table identifier for '{et.DisplayName()}'.");
            return prop.GetColumnName(table)
                ?? throw new InvalidOperationException($"Property '{prop.Name}' has no column mapping on table '{table.DisplayName()}'.");
        });

    public static IProperty ResolveSelector(LambdaExpression selector, IEntityType entityType)
    {
        var body = UnwrapConvert(selector.Body);

        if (body is MemberExpression { Expression: ParameterExpression } member)
        {
            return ResolvePropertyOrThrow(member, entityType);
        }

        if (body is NewExpression newExpression && newExpression.Members is not null)
        {
            throw new NotSupportedException("Anonymous-type selectors are not valid here; use a single property selector like x => x.Prop.");
        }

        throw new NotSupportedException(
            $"Selector '{selector}' is not supported. Only direct member selectors like x => x.Prop are supported.");
    }

    public static object? ConvertToProvider(IProperty property, object? value)
    {
        if (value is null)
        {
            return null;
        }

        // SAFETY S4: cached converter is identical to direct lookup; preserves enum/value-converter correctness
        var converter = _converterCache.GetOrAdd(property, static p => p.GetValueConverter() ?? p.GetRelationalTypeMapping().Converter);

        if (converter is not null)
        {
            return converter.ConvertToProvider(value);
        }

        if (value.GetType().IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(value.GetType());
            return Convert.ChangeType(value, underlying);
        }

        return value;
    }

    public static BoundAssignment CreateAssignment(IProperty property, object? value)
    {
        EnsureWritable(property);
        return new BoundAssignment { Property = property, Value = ConvertToProvider(property, value) };
    }

    public static void EnsureWritable(IProperty property)
    {
        if (property.ValueGenerated != ValueGenerated.Never)
        {
            throw new InvalidOperationException(
                $"Column '{GetColumnName(property, property.DeclaringType as IEntityType ?? throw new InvalidOperationException())}' for property '{property.Name}' is store-generated and cannot be assigned by bulk operations.");
        }
    }

    public static void AddDiscriminatorPart(BoundOperation operation)
    {
        var entityType = operation.EntityType;
        var discriminator = entityType.FindDiscriminatorProperty();

        if (discriminator is null || entityType.GetDiscriminatorValue() is not { } discriminatorValue)
        {
            return;
        }

        operation.PredicateParts.Add(new SqlBinaryNode(
            SqlBinaryOperator.Equal,
            new SqlColumnNode(discriminator),
            new SqlParameterNode(ConvertToProvider(discriminator, discriminatorValue))));
    }

    public static object? ReadMemberValue(IProperty property, object instance)
    {
        if (property.PropertyInfo is null && property.FieldInfo is null)
        {
            throw new InvalidOperationException($"Property '{property.Name}' has no CLR member and cannot be read from an entity instance.");
        }

        // SAFETY S2: cached getter returns identical value to reflection; preserves sequential semantics
        var getter = _getterCache.GetOrAdd(property, static p =>
        {
            if (p.PropertyInfo is { } propertyInfo)
            {
                var instanceParam = Expression.Parameter(typeof(object), "instance");
                var typedInstance = Expression.Convert(instanceParam, propertyInfo.DeclaringType!);
                var propertyAccess = Expression.Property(typedInstance, propertyInfo);
                var boxed = Expression.Convert(propertyAccess, typeof(object));
                return Expression.Lambda<Func<object, object?>>(boxed, instanceParam).Compile();
            }

            // FieldInfo is guaranteed non-null here because of the early throw above
            var fieldInfo = p.FieldInfo!;
            var fieldParam = Expression.Parameter(typeof(object), "instance");
            var typedFieldInstance = Expression.Convert(fieldParam, fieldInfo.DeclaringType!);
            var fieldAccess = Expression.Field(typedFieldInstance, fieldInfo);
            var fieldBoxed = Expression.Convert(fieldAccess, typeof(object));
            return Expression.Lambda<Func<object, object?>>(fieldBoxed, fieldParam).Compile();
        });

        return getter(instance);
    }

    public static bool IsBindableScalar(IProperty property)
        => !property.IsShadowProperty()
           && property.PropertyInfo is not null
           && !property.IsPrimaryKey()
           && property.ValueGenerated == ValueGenerated.Never;

    /// <summary>
    /// Like <see cref="IsBindableScalar"/>, but allows primary keys. Used for upsert
    /// INSERT column lists where a non-store-generated key must be written explicitly.
    /// </summary>
    public static bool IsInsertBindable(IProperty property)
        => !property.IsShadowProperty()
           && property.PropertyInfo is not null
           && property.ValueGenerated == ValueGenerated.Never;

    private static IProperty ResolvePropertyOrThrow(MemberExpression member, IEntityType entityType)
        => entityType.FindProperty(member.Member)
           ?? entityType.FindProperty(member.Member.Name)
           ?? throw new InvalidOperationException(
               $"Property '{entityType.DisplayName()}.{member.Member.Name}' is not part of the EF model and cannot be used in bulk operations.");

    private static Expression UnwrapConvert(Expression expression)
    {
        while (expression is UnaryExpression
               {
                   NodeType: ExpressionType.Convert
                   or ExpressionType.ConvertChecked
                   or ExpressionType.TypeAs
               } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }
}
