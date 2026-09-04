using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Integration.Npgsql;

public class NpgsqlTransactionAndSemanticsTests : NpgsqlTestBase
{
    public NpgsqlTransactionAndSemanticsTests(NpgsqlFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ThrowIfZeroAffected_throws()
    {
        RequireDatabase();
        await using var ctx = Fixture.CreateContext();
        await ctx.Items.ExecuteDeleteAsync();
        ctx.Items.Add(new Item { Id = 9501, Key1 = "a" });
        await ctx.SaveChangesAsync();

        await using var ctx2 = Fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BulkZeroRowsAffectedException>(() => ctx2.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 9501).Set(x => x.Key1, "b"));
            b.Update<Item>(op => op.Where(x => x.Id == 9999).Set(x => x.Key1, "c"));
        }, new BulkExecuteOptions { ThrowIfZeroAffected = true }));
        Assert.Equal(1, ex.OperationIndex);
    }

    [Fact]
    public async Task ThrowIfZeroAffected_with_transaction_can_rollback()
    {
        RequireDatabase();
        const int id = 9502;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key1 = "keep" });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await using var tx = await ctx2.Database.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<BulkZeroRowsAffectedException>(() => ctx2.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, "changed"));
            b.Delete<Item>(op => op.Where(x => x.Id == 9999));
        }, new BulkExecuteOptions { ThrowIfZeroAffected = true }));
        Assert.Equal(1, ex.OperationIndex);
        await tx.RollbackAsync();

        await using var verify = Fixture.CreateContext();
        Assert.Equal("keep", (await verify.Items.SingleAsync(x => x.Id == id)).Key1);
    }

    [Fact]
    public async Task Ambient_transaction_is_piggybacked()
    {
        RequireDatabase();
        const int id = 9503;
        await using (var ctx = Fixture.CreateContext())
        {
            await ctx.Items.ExecuteDeleteAsync();
            ctx.Items.Add(new Item { Id = id, Key1 = "orig" });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = Fixture.CreateContext();
        await using var tx = await ctx2.Database.BeginTransactionAsync();
        await ctx2.BulkExecuteAsync(b => b.Update<Item>(op => op.Where(x => x.Id == id).Set(x => x.Key1, "txed")));
        await tx.CommitAsync();

        await using var verify = Fixture.CreateContext();
        Assert.Equal("txed", (await verify.Items.SingleAsync(x => x.Id == id)).Key1);
    }
}
