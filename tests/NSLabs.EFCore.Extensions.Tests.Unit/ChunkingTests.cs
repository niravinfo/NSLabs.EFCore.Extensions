namespace NSLabs.EFCore.Extensions.Tests.Unit;

public class ChunkingTests
{
    [Fact]
    public void Parameter_budget_splits_operations_into_ordered_chunks()
    {
        var chunks = Harness.Generate(
            b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "v"));
                b.Update<Item>(op => op.Where(x => x.Id == 2).Set(x => x.Key1, "w").Set(x => x.Key2, 2));
            },
            new BulkExecuteOptions { MaxParametersPerCommand = 4 });

        Assert.Equal(2, chunks.Count);

        Assert.Equal([0], chunks[0].OperationIndices);
        Assert.Contains("SELECT @rc0 AS Op0;", chunks[0].CommandText);
        Assert.Equal(2, chunks[0].Parameters.Count);

        Assert.Equal([1], chunks[1].OperationIndices);
        Assert.Contains("SELECT @rc1 AS Op1;", chunks[1].CommandText);
        Assert.DoesNotContain("@rc0", chunks[1].CommandText);
        Assert.Equal(3, chunks[1].Parameters.Count);
    }

    [Fact]
    public void Chunked_plans_keep_global_parameter_naming_per_chunk_restart()
    {
        var chunks = Harness.Generate(
            b =>
            {
                foreach (var id in Enumerable.Range(1, 3))
                {
                    var captured = id;
                    b.Update<Item>(op => op.Where(x => x.Id == captured).Set(x => x.Key1, "v" + id));
                }
            },
            new BulkExecuteOptions { MaxParametersPerCommand = 4 });

        Assert.Equal(2, chunks.Count);

        Assert.All(chunks, chunk =>
        {
            var names = chunk.Parameters.Select(p => p.Name).ToArray();
            Assert.Equal(names, names.Distinct().ToArray());
        });
    }

    [Fact]
    public void Single_operation_exceeding_budget_throws_with_guidance()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Harness.Generate(
            b => b.Update<Item>(op =>
            {
                op.Where(x => x.Id == 1);
                op.Set(x => x.Key1, "v");
                op.Set(x => x.Key2, 7);
            }),
            new BulkExecuteOptions { MaxParametersPerCommand = 2 }));

        Assert.Contains("exceeds MaxParametersPerCommand", ex.Message);
    }

    [Fact]
    public void Upsert_rows_split_across_chunks_within_parameter_budget()
    {
        var chunks = Harness.Generate(
            b => b.Upsert<Customer>(u => u
                .On(x => x.Code)
                .Values(new[]
                {
                    new Customer { Code = "A" },
                    new Customer { Code = "B" },
                    new Customer { Code = "C" }
                })),
            new BulkExecuteOptions { MaxParametersPerCommand = 8 });

        Assert.Equal(2, chunks.Count);

        // 3 params per row: two rows (6) fit in budget, the third spills to a second chunk.
        Assert.Equal([0], chunks[0].OperationIndices);
        Assert.Contains("(VALUES (@p0, @p1, @p2), (@p3, @p4, @p5))", chunks[0].CommandText);

        Assert.Equal([0], chunks[1].OperationIndices);
        Assert.Contains("(VALUES (@p0, @p1, @p2))", chunks[1].CommandText);
        Assert.DoesNotContain("@rc0 AS Op0, @rc0", chunks[1].CommandText);
    }

    [Fact]
    public void Upsert_row_wider_than_budget_throws_with_guidance()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Harness.Generate(
            b => b.Upsert<Item>(u => u
                .On(x => x.Id)
                .Values(new Item { Id = 1 })),
            new BulkExecuteOptions { MaxParametersPerCommand = 3 }));

        Assert.Contains("single upsert row", ex.Message);
        Assert.Contains("exceeds MaxParametersPerCommand", ex.Message);
    }

    [Fact]
    public void Upsert_keeps_submission_order_when_mixed_with_updates_across_chunks()
    {
        var chunks = Harness.Generate(
            b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "u"));
                b.Upsert<Customer>(u => u
                    .On(x => x.Code)
                    .Values(Enumerable.Range(0, 5).Select(i => new Customer { Code = $"C{i}" }).ToArray()));
            },
            new BulkExecuteOptions { MaxParametersPerCommand = 7 });

        // Update costs 2; upsert rows cost 3 each. Chunk1: update + 1 row (5).
        // Remaining rows fill two rows (6) per subsequent chunk.
        Assert.Equal([0, 1], chunks[0].OperationIndices);
        Assert.All(chunks.Skip(1), chunk => Assert.Equal([1], chunk.OperationIndices));

        Assert.Contains("UPDATE [Items]", chunks[0].CommandText);
        Assert.Contains("MERGE INTO [Customers]", chunks[0].CommandText);
        Assert.All(chunks.Skip(1), chunk => Assert.Contains("MERGE INTO [Customers]", chunk.CommandText));
    }

    [Fact]
    public void Chunked_plans_parameter_names_are_unique_per_chunk()
    {
        var chunks = Harness.Generate(b =>
        {
            for (var i = 0; i < 10; i++)
            {
                var captured = i;
                b.Update<Item>(op => op.Where(x => x.Id == captured).Set(x => x.Key1, "v"));
            }
        }, new BulkExecuteOptions { MaxParametersPerCommand = 10 });

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            var names = chunk.Parameters.Select(p => p.Name).ToArray();
            Assert.Equal(names.Length, names.Distinct().Count());
            Assert.All(names, n => Assert.StartsWith("@p", n));
        }
    }

    [Fact]
    public void Parallel_generation_produces_independent_chunks()
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();
        System.Threading.Tasks.Parallel.For(0, 20, i =>
        {
            var chunks = Harness.Generate(b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == i).Set(x => x.Key1, $"V{i}"));
            });
            results.Add(chunks[0].CommandText);
        });
        Assert.Equal(20, results.Count);
        Assert.All(results, sql => Assert.Contains("UPDATE [Items]", sql));
    }

    [Fact]
    public void Large_entity_batch_chunking_accounts_all_parameters()
    {
        var rows = Enumerable.Range(1, 100).Select(i => new Item { Id = i, Key1 = $"K{i}", Key2 = i }).ToArray();
        var chunks = Harness.Generate(b => b.Update<Item>(rows), new BulkExecuteOptions { MaxParametersPerCommand = 20000 });
        Assert.True(chunks.Count >= 1);
        Assert.True(chunks.Sum(c => c.Parameters.Count) > 100);
        Assert.All(chunks, c => Assert.NotEmpty(c.CommandText));
    }
}
