using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit;

public class ValidationTests
{
    [Fact]
    public void Duplicate_upsert_keys_within_one_operation_throw_at_execute()
    {
        using var context = new TestDbContext();
        var batch = new BulkBatch(context);
        batch.Upsert<Customer>(u => u
            .On(x => x.Code)
            .Values(
                new[]
                {
                    new Customer { Code = "DUP", Name = "First" },
                    new Customer { Code = "DUP", Name = "Second" }
                }));

        var ex = Assert.Throws<InvalidOperationException>(() => BulkBatch.ValidateUniqueUpsertKeys(batch.Operations));

        Assert.Contains("'Customers'", ex.Message);
        Assert.Contains("operation #0 (row 0)", ex.Message);
        Assert.Contains("operation #0 (row 1)", ex.Message);
    }

    [Fact]
    public void Duplicate_upsert_keys_across_operations_name_both_indices()
    {
        using var context = new TestDbContext();
        var batch = new BulkBatch(context);
        batch.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "x"));
        batch.Upsert<Customer>(u => u.On(x => x.Code).Values(new Customer { Code = "DUP" }));
        batch.Upsert<Customer>(u => u.On(x => x.Code).Values(new[] { new Customer { Code = "OTHER" }, new Customer { Code = "DUP" } }));

        var ex = Assert.Throws<InvalidOperationException>(() => BulkBatch.ValidateUniqueUpsertKeys(batch.Operations));

        Assert.Contains("operation #1 (row 0)", ex.Message);
        Assert.Contains("operation #2 (row 1)", ex.Message);
    }

    [Fact]
    public void Distinct_upsert_keys_and_different_conflict_shapes_pass_validation()
    {
        using var context = new TestDbContext();
        var batch = new BulkBatch(context);
        batch.Upsert<Customer>(u => u
            .On(x => x.Code)
            .Values(new[] { new Customer { Code = "A" }, new Customer { Code = "B" } }));
        // Same table, different shape: validated independently.
        batch.Upsert<Customer>(u => u.On(x => new { x.Name }).Values(new Customer { Name = "N" }));

        BulkBatch.ValidateUniqueUpsertKeys(batch.Operations);
    }

    [Fact]
    public void Duplicate_set_assignments_fail_at_build_time_before_any_sql()
    {
        using var context = new TestDbContext();
        var batch = new BulkBatch(context);

        // Unlike EF Core's ExecuteUpdateAsync (which emits both SetProperty calls and lets
        // SQL Server fail at execution time with a cryptic SqlException), the batch must
        // reject the duplicate immediately with a clear error, before any database access.
        var ex = Assert.Throws<InvalidOperationException>(() => batch.Update<Item>(op => op
            .Where(x => x.Id == 1)
            .Set(x => x.Key1, "first")
            .Set(x => x.Key1, "second")));

        Assert.Contains("assigns 'Key1' more than once", ex.Message);
    }

    [Fact]
    public async Task Duplicate_key_validation_runs_before_provider_or_database_access()
    {
        using var context = new TestDbContext();
        var batch = new BulkBatch(context);
        batch.Upsert<Customer>(u => u.On(x => x.Code).Values(new[] { new Customer { Code = "DUP" }, new Customer { Code = "DUP" } }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteAsync());

        Assert.Contains("Duplicate upsert match-key", ex.Message);
    }

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
    public void Set_targeting_conflict_column_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Harness.GenerateSingle(b => b
            .Upsert<Customer>(u => u
                .On(x => x.Code)
                .Set(x => x.Code, "X")
                .Values(new Customer { Code = "A" }))));

        Assert.Contains("conflict column 'Code'", ex.Message);
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
