using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.SqlServer;

public class UpsertExecutionTests(SqlServerFixture fixture) : SqlServerTestBase(fixture)
{
    [SkippableFact]
    public async Task Fresh_conflict_keys_take_the_insert_path()
    {
        RequireDatabase();

        await using var context = Fixture.CreateContext();
        var result = await context.Customers.BulkUpsertAsync(b => b
            .Add(u => u
                .On(x => x.Code)
                .Values(new[]
                {
                    new Customer { Code = "UP-9501", Name = "Inserted", Active = true },
                    new Customer { Code = "UP-9502", Name = "InsertedToo", Active = false }
                })));

        Assert.Equal(2, result.TotalRowsAffected);
        Assert.Equal(2, result.Operations[0].RowsAffected);

        await using var verify = Fixture.CreateContext();
        var inserted = await verify.Customers.AsNoTracking()
            .Where(x => x.Code == "UP-9501" || x.Code == "UP-9502")
            .OrderBy(x => x.Code)
            .ToListAsync();

        Assert.Equal(2, inserted.Count);
        Assert.Equal("Inserted", inserted[0].Name);
        Assert.True(inserted[0].Active);
        Assert.Equal("InsertedToo", inserted[1].Name);
        Assert.False(inserted[1].Active);
    }

    [SkippableFact]
    public async Task Existing_conflict_keys_take_the_update_path()
    {
        RequireDatabase();

        await using (var seed = Fixture.CreateContext())
        {
            seed.Customers.Add(new Customer { Code = "UP-9510", Name = "Original", Active = true });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Customers.BulkUpsertAsync(b => b
                .Add(u => u
                    .On(x => x.Code)
                    .Values(new Customer { Code = "UP-9510", Name = "Renamed", Active = false })));

            Assert.Equal(1, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var reloaded = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == "UP-9510");
        Assert.Equal("Renamed", reloaded.Name);
        Assert.False(reloaded.Active);
    }

    [SkippableFact]
    public async Task One_merge_statement_handles_mixed_insert_and_update_paths()
    {
        RequireDatabase();

        const string existingCode = "UP-9520";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Customers.Add(new Customer { Code = existingCode, Name = "Before", Active = true });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Customers.BulkUpsertAsync(b => b
                .Add(u => u
                    .On(x => x.Code)
                    .Values(new[]
                    {
                        new Customer { Code = existingCode, Name = "After", Active = false },
                        new Customer { Code = "UP-9521", Name = "BrandNew", Active = true }
                    })));

            Assert.Equal(2, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var updated = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == existingCode);
        Assert.Equal("After", updated.Name);
        Assert.False(updated.Active);

        var inserted = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == "UP-9521");
        Assert.Equal("BrandNew", inserted.Name);
    }

    [SkippableFact]
    public async Task Composite_conflict_target_matches_all_columns_before_updating()
    {
        RequireDatabase();

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = 9701, Key1 = "dup", Key2 = 1, Key3 = 0 });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Items.BulkUpsertAsync(b => b
                .Add(u => u
                    .On(x => new { x.Key1, x.Key2 })
                    .Values(new[]
                    {
                        // (Key1="dup", Key2=1) exists: takes the update path.
                        new Item { Id = 9701, Key1 = "dup", Key2 = 1, Key3 = 42 },
                        // Shares Key1="dup" but Key2=2 differs: must take the insert path,
                        // proving the ON clause requires every conflict column to match.
                        new Item { Id = 9702, Key1 = "dup", Key2 = 2, Key3 = 7 }
                    })));

            Assert.Equal(2, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Equal(42, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == 9701)).Key3);
        Assert.Equal(7, (await verify.Items.AsNoTracking().SingleAsync(x => x.Id == 9702)).Key3);
    }

    [SkippableFact]
    public async Task Guard_blocks_matched_update_but_not_insert()
    {
        RequireDatabase();

        const string guardedCode = "UP-9530";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Customers.Add(new Customer { Code = guardedCode, Name = "Protected", Active = true });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            // Guard refuses to touch active customers; a different missing key still inserts.
            var result = await context.Customers.BulkUpsertAsync(b =>
            {
                b.Add(op => op
                    .On(x => x.Code)
                    .WhenMatched(x => !x.Active)
                    .Values(new Customer { Code = guardedCode, Name = "Overwritten" }));
                b.Add(op => op
                    .On(x => x.Code)
                    .WhenMatched(x => !x.Active)
                    .Values(new Customer { Code = "UP-9531", Name = "FreshRow" }));
            });

            Assert.Equal(1, result.TotalRowsAffected);
            Assert.Equal(0, result.Operations[0].RowsAffected);
            Assert.Equal(1, result.Operations[1].RowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var untouched = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == guardedCode);
        Assert.Equal("Protected", untouched.Name);

        var inserted = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == "UP-9531");
        Assert.Equal("FreshRow", inserted.Name);
    }

    [SkippableFact]
    public async Task Explicit_set_constants_apply_on_match_only()
    {
        RequireDatabase();

        const string code = "UP-9540";
        await using (var seed = Fixture.CreateContext())
        {
            seed.Customers.Add(new Customer { Code = code, Name = "Before", Active = true });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.Customers.BulkUpsertAsync(b => b
                .Add(u => u
                    .On(x => x.Code)
                    .Set(x => x.Active, false)
                    .Values(new Customer { Code = code, Name = "IgnoredOnMatch" })));

            Assert.Equal(1, result.TotalRowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        var reloaded = await verify.Customers.AsNoTracking().SingleAsync(x => x.Code == code);
        Assert.False(reloaded.Active);
        Assert.Equal("Before", reloaded.Name);
    }

    [SkippableFact]
    public async Task Upsert_mixes_with_update_and_delete_in_one_round_trip()
    {
        RequireDatabase();

        await using (var seed = Fixture.CreateContext())
        {
            seed.Items.Add(new Item { Id = 9600, Key1 = "legacy", Key2 = 1, Key3 = 1, Active = true });
            await seed.SaveChangesAsync();
        }

        await using (var context = Fixture.CreateContext())
        {
            var result = await context.BulkExecuteAsync(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Key1 == "legacy").Set(x => x.Key3, 77));
                b.Upsert<Customer>(u => u
                    .On(x => x.Code)
                    .Values(new Customer { Code = "UP-9601", Name = "FromMixedBatch", Active = true }));
                b.Delete<Item>(op => op.Where(x => x.Id == 9600));
            });

            Assert.Equal(3, result.TotalRowsAffected);
            Assert.Equal(1, result.Operations[0].RowsAffected);
            Assert.Equal(1, result.Operations[1].RowsAffected);
            Assert.Equal(1, result.Operations[2].RowsAffected);
        }

        await using var verify = Fixture.CreateContext();
        Assert.Null(await verify.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 9600));
        Assert.NotNull(await verify.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Code == "UP-9601"));
    }
}
