namespace EF.Core.Extensions.Tests.Unit;

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
                for (var i = 0; i < 11; i++)
                {
                    op.Set(x => x.Key1, "v");
                }
            }),
            new BulkExecuteOptions { MaxParametersPerCommand = 10 }));

        Assert.Contains("exceeds MaxParametersPerCommand", ex.Message);
    }
}
