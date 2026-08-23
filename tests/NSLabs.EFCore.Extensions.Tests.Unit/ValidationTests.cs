using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit;

public class ValidationTests
{
    [Fact]
    public void Unknown_property_in_where_throws_clear_exception()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.NotMapped == "x")
                .Set(x => x.Key1, "y"))));

        Assert.Contains("NotMapped", ex.Message);
        Assert.Contains("Item", ex.Message);
    }

    [Fact]
    public void Set_on_store_generated_column_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Id == 1)
                .Set(x => x.CreatedAt, DateTime.Now))));

        Assert.Contains("store-generated", ex.Message);
    }

    [Fact]
    public void Update_without_where_throws()
    {
        Assert.Throws<InvalidOperationException>(() => Harness.GenerateSingle(b => b
            .Update<Item>(op => op.Set(x => x.Key1, "x"))));
    }

    [Fact]
    public void Update_without_set_throws()
    {
        Assert.Throws<InvalidOperationException>(() => Harness.GenerateSingle(b => b
            .Update<Item>(op => op.Where(x => x.Id == 1))));
    }

    [Fact]
    public void Unsupported_predicate_construct_throws_not_supported()
    {
        Assert.Throws<NotSupportedException>(() => Harness.GenerateSingle(b => b
            .Update<Item>(op => op
                .Where(x => x.Key1.Trim() == "a")
                .Set(x => x.Key3, 1))));
    }

    [Fact]
    public void Upsert_generation_is_deferred_to_m2()
    {
        Assert.Throws<NotImplementedException>(() => Harness.GenerateSingle(b => b
            .Upsert<Item>(u => u
                .On(x => x.Id)
                .Values(new Item { Id = 42, Key1 = "k" }))));
    }

    [Fact]
    public async Task Empty_batch_executes_as_no_op_without_provider_or_database_access()
    {
        using var context = new TestDbContext();
        var result = await context.CreateBulkBatch().ExecuteAsync();

        Assert.Equal(0, result.TotalRowsAffected);
        Assert.Empty(result.Operations);
    }
}
