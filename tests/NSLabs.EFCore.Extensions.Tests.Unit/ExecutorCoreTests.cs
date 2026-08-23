using System.Data;
using EF = NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit;

public class ExecutorCoreTests
{
    private static (FakeAdo.Connection Connection, List<EF.SqlChunkPlan> Chunks, Dictionary<int, int> Counts) Arrange()
    {
        var connection = new FakeAdo.Connection();
        var chunks = new List<EF.SqlChunkPlan>
        {
            new()
            {
                CommandText = "DECLARE @rc0 int;\nUPDATE [Items] SET [Key1] = @p0 WHERE [Id] = @p1;\nSET @rc0 = @@ROWCOUNT;\nSELECT @rc0 AS Op0;",
                Parameters =
                [
                    new EF.SqlParam("@p0", "Value1"),
                    new EF.SqlParam("@p1", 6)
                ],
                OperationIndices = [0]
            },
            new()
            {
                CommandText = "DECLARE @rc1 int;\nUPDATE [Orders] SET [Amount] = @p2 WHERE [OrderNo] = @p3;\nSET @rc1 = @@ROWCOUNT;\nSELECT @rc1 AS Op1;",
                Parameters =
                [
                    new EF.SqlParam("@p2", 10.5m),
                    new EF.SqlParam("@p3", null)
                ],
                OperationIndices = [1]
            }
        };
        return (connection, chunks, []);
    }

    private static void ScriptRowCounts(FakeAdo.Connection connection, params int[][] rowsPerCommand)
    {
        var queue = new Queue<int[]>(rowsPerCommand);
        connection.ReaderFactory = _ =>
        {
            var row = queue.Dequeue();
            return new FakeAdo.FakeReader(row.Select((_, i) => $"Op{i}").ToArray(), [row]);
        };
    }

    [Fact]
    public async Task Executes_each_chunk_with_bound_parameters_and_reads_global_counts()
    {
        var (connection, chunks, counts) = Arrange();
        ScriptRowCounts(connection, [1], [7]);

        var logged = new List<string>();
        var options = new BulkExecuteOptions { OnCommandText = logged.Add };
        var transaction = new FakeAdo.Transaction(connection, IsolationLevel.ReadCommitted);

        await EF.SqlServerExecutor.ExecuteCoreAsync(connection, transaction, chunks, counts, options, CancellationToken.None);

        Assert.Equal(2, connection.ExecutedCommands.Count);
        Assert.Equal(chunks[0].CommandText, connection.ExecutedCommands[0].CommandText);
        Assert.Equal(chunks[1].CommandText, connection.ExecutedCommands[1].CommandText);
        Assert.True(logged.Count == chunks.Count);

        var firstParams = (FakeAdo.ParameterCollection)connection.ExecutedCommands[0].Parameters;
        Assert.Equal(2, firstParams.Count);
        Assert.Equal("@p0", firstParams[0].ParameterName);
        Assert.Equal("Value1", firstParams[0].Value);
        Assert.Equal(6, firstParams[1].Value);

        var secondParams = (FakeAdo.ParameterCollection)connection.ExecutedCommands[1].Parameters;
        Assert.Equal(DBNull.Value, secondParams[1].Value);
        Assert.Equal(transaction, connection.ExecutedCommands[1].Transaction);

        Assert.Equal(1, counts[0]);
        Assert.Equal(7, counts[1]);
    }

    [Fact]
    public async Task Missing_rowcount_resultset_throws_expected_error()
    {
        var (connection, chunks, counts) = Arrange();
        connection.ReaderFactory = _ => new FakeAdo.FakeReader([], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EF.SqlServerExecutor.ExecuteCoreAsync(connection, null, chunks, counts, new BulkExecuteOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task Empty_rowcount_row_throws_expected_error()
    {
        var (connection, chunks, counts) = Arrange();
        connection.ReaderFactory = _ => new FakeAdo.FakeReader(["Op0"], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EF.SqlServerExecutor.ExecuteCoreAsync(connection, null, chunks, counts, new BulkExecuteOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task Reader_failure_propagates_after_partial_chunks()
    {
        var (connection, chunks, counts) = Arrange();
        ScriptRowCounts(connection, [3]);
        connection.ReaderFactory = _ =>
        {
            if (connection.ExecutedCommands.Count == 1)
            {
                return new FakeAdo.FakeReader(["Op1"], [[3]]);
            }

            throw new DataException("simulated failure");
        };

        await Assert.ThrowsAsync<DataException>(() =>
            EF.SqlServerExecutor.ExecuteCoreAsync(connection, null, chunks, counts, new BulkExecuteOptions(), CancellationToken.None));

        Assert.Equal(2, connection.ExecutedCommands.Count);
        Assert.True(counts.ContainsKey(0));
        Assert.False(counts.ContainsKey(1));
    }
}
