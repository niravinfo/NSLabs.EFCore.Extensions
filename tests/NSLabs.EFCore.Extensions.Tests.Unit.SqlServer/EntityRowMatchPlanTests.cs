using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

public class EntityRowMatchPlanTests
{
    [Fact]
    public void Direct_same_property_equality_is_supported()
    {
        using var context = CreateContext();
        var entityType = ModelBinder.ResolveEntityType<Item>(context.Model);
        Expression<Func<Item, Item, bool>> expression = (row, x) => x.Id == row.Id;

        var plan = EntityRowMatchPlan.TryCreate(expression, entityType);

        Assert.NotNull(plan);
        var comparison = Assert.IsType<SqlBinaryNode>(plan.Bind(new Item { Id = 5 }));
        var parameter = Assert.IsType<SqlParameterNode>(comparison.Right);
        Assert.Equal(5, Assert.IsType<int>(parameter.Value));
    }

    [Fact]
    public void Direct_nullable_equality_binds_to_a_null_check()
    {
        using var context = CreateContext();
        var entityType = ModelBinder.ResolveEntityType<Item>(context.Model);
        Expression<Func<Item, Item, bool>> expression = (row, x) => x.ParentId == row.ParentId;

        var plan = EntityRowMatchPlan.TryCreate(expression, entityType);

        Assert.NotNull(plan);
        var nullCheck = Assert.IsType<SqlNullCheckNode>(plan.Bind(new Item { ParentId = null }));
        Assert.Equal(nameof(Item.ParentId), nullCheck.Property.Name);
        Assert.False(nullCheck.IsNotNull);
    }

    [Fact]
    public void Direct_match_applies_the_property_value_converter()
    {
        var options = new DbContextOptionsBuilder<ConvertedMatchDbContext>()
            .UseSqlServer("Server=tcp:localhost,1433;Database=BulkExtensionsTest;User Id=test;Password=test;TrustServerCertificate=True;")
            .Options;
        using var context = new ConvertedMatchDbContext(options);
        var entityType = ModelBinder.ResolveEntityType<ConvertedMatchEntity>(context.Model);
        var token = Guid.NewGuid();
        Expression<Func<ConvertedMatchEntity, ConvertedMatchEntity, bool>> expression =
            (row, x) => x.Token == row.Token;

        var plan = EntityRowMatchPlan.TryCreate(expression, entityType);

        Assert.NotNull(plan);
        var comparison = Assert.IsType<SqlBinaryNode>(plan.Bind(new ConvertedMatchEntity { Token = token }));
        var parameter = Assert.IsType<SqlParameterNode>(comparison.Right);
        Assert.Equal(token.ToString(), Assert.IsType<string>(parameter.Value));
    }

    [Fact]
    public void Non_fast_path_shapes_are_rejected_as_a_group()
    {
        using var context = CreateContext();
        var entityType = ModelBinder.ResolveEntityType<Item>(context.Model);
        const int capturedId = 5;

        Assert.Null(EntityRowMatchPlan.TryCreate(
            (Expression<Func<Item, Item, bool>>)((row, x) => x.Id == row.Id && x.Key2 > row.Key2),
            entityType));
        Assert.Null(EntityRowMatchPlan.TryCreate(
            (Expression<Func<Item, Item, bool>>)((row, x) => x.Id == row.Id || x.Key2 == row.Key2),
            entityType));
        Assert.Null(EntityRowMatchPlan.TryCreate(
            (Expression<Func<Item, Item, bool>>)((row, x) => x.Id == capturedId),
            entityType));
        Assert.Null(EntityRowMatchPlan.TryCreate(
            (Expression<Func<Item, Item, bool>>)((row, x) => x.Id == row.Key2),
            entityType));
        Assert.Null(EntityRowMatchPlan.TryCreate(
            (Expression<Func<Item, Item, bool>>)((row, x) => x.Key1.StartsWith(row.Key1)),
            entityType));
        Assert.Null(EntityRowMatchPlan.TryCreate(
            (Expression<Func<Item, Item, bool>>)((row, x) => (int)x.Status == (int)row.Status),
            entityType));
        Assert.Null(EntityRowMatchPlan.TryCreate(
            (Expression<Func<Item, Item, bool>>)((row, x) => x.NotMapped == row.NotMapped),
            entityType));
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer("Server=tcp:localhost,1433;Database=BulkExtensionsTest;User Id=test;Password=test;TrustServerCertificate=True;")
            .Options;
        return new TestDbContext(options);
    }

    private sealed class ConvertedMatchEntity
    {
        public int Id { get; set; }

        public Guid Token { get; set; }
    }

    private sealed class ConvertedMatchDbContext(DbContextOptions<ConvertedMatchDbContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<ConvertedMatchEntity>();
            entity.ToTable("ConvertedMatchEntities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Token)
                .HasConversion<string>();
        }
    }
}
