using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

internal static class ModelBinder
{
    public static IEntityType ResolveEntityType<TEntity>(IModel model) where TEntity : class
        => model.FindEntityType(typeof(TEntity))
           ?? throw new InvalidOperationException(
               $"Entity '{typeof(TEntity).Name}' is not part of the DbContext model. Register it via DbSet<TEntity> or modelBuilder.Entity<{typeof(TEntity).Name}>().");

    public static string GetTableName(IEntityType entityType)
        => entityType.GetTableName()
           ?? throw new InvalidOperationException($"Entity '{entityType.DisplayName()}' is not mapped to a relational table.");

    public static string GetColumnName(IProperty property, IEntityType entityType)
    {
        var table = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)
            ?? throw new InvalidOperationException($"Cannot resolve table identifier for '{entityType.DisplayName()}'.");

        return property.GetColumnName(table)
            ?? throw new InvalidOperationException($"Property '{property.Name}' has no column mapping on table '{table.DisplayName}'.");
    }

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

        var converter = property.GetValueConverter() ?? property.GetRelationalTypeMapping().Converter;

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
        if (property.PropertyInfo is { } propertyInfo)
        {
            return propertyInfo.GetValue(instance);
        }

        if (property.FieldInfo is { } fieldInfo)
        {
            return fieldInfo.GetValue(instance);
        }

        throw new InvalidOperationException($"Property '{property.Name}' has no CLR member and cannot be read from an entity instance.");
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
