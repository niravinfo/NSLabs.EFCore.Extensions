namespace NSLabs.EFCore.Extensions.Tests.Unit.Sqlite;

public class SqliteChunkingTests
{
    [Fact]
    public void Parameter_budget_clamps_to_999()
    {
        var chunks = SqliteHarness.Generate(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "v"));
        }, new BulkExecuteOptions { MaxParametersPerCommand = 2000 });

        // Effective limit is 999, single op cost 2 <999 so 1 chunk
        Assert.Single(chunks);
    }

    [Fact]
    public void Single_operation_exceeding_999_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SqliteHarness.Generate(
            b => b.Update<Item>(op =>
            {
                op.Where(x => x.Id == 1);
                op.Set(x => x.Key1, "v");
                op.Set(x => x.Key2, 7);
            }),
            new BulkExecuteOptions { MaxParametersPerCommand = 2 }));
        Assert.Contains("exceeds MaxParametersPerCommand", ex.Message);
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void Upsert_rows_split_respecting_999()
    {
        var chunks = SqliteHarness.Generate(b => b.Upsert<Customer>(u => u
            .MatchOn(x => x.Code)
            .Insert(new[]
            {
                new Customer { Code = "A" },
                new Customer { Code = "B" },
                new Customer { Code = "C" }
            })), new BulkExecuteOptions { MaxParametersPerCommand = 6 });

        // 3 cols per row, max 6 => 2 rows per chunk => 2 chunks (2+1)
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Parameters.Count <= 6));
    }

    [Fact]
    public void Chunk_params_unique_per_chunk()
    {
        var chunks = SqliteHarness.Generate(b =>
        {
            for (var i = 0; i < 5; i++)
            {
                var c = i;
                b.Update<Item>(op => op.Where(x => x.Id == c).Set(x => x.Key1, "v"));
            }
        }, new BulkExecuteOptions { MaxParametersPerCommand = 4 });

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            var names = chunk.Parameters.Select(p => p.Name).ToArray();
            Assert.Equal(names.Length, names.Distinct().Count());
            Assert.All(names, n => Assert.StartsWith("@p", n));
        }
    }

    [Fact]
    public void Large_batch_reports_all_params()
    {
        var rows = Enumerable.Range(1, 50).Select(i => new Item { Id = i, Key1 = $"K{i}", Key2 = i }).ToArray();
        var chunks = SqliteHarness.Generate(b => b.Update<Item>(rows), new BulkExecuteOptions { MaxParametersPerCommand = 999 });
        Assert.True(chunks.Sum(c => c.Parameters.Count) > 50);
        Assert.All(chunks, c => Assert.True(c.Parameters.Count <= 999));
    }
}
