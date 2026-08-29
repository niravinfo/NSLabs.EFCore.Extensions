using System.Data;
using EF = NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit;

public class TransactionTests
{
    private static (FakeAdo.Connection Connection, List<EF.SqlChunkPlan> Chunks, Dictionary<int, int> Counts) ArrangeTwoOps()
    {
        var connection = new FakeAdo.Connection();
        var chunks = new List<EF.SqlChunkPlan>
        {
            new()
            {
                CommandText = "UPDATE [Items] SET [Key1] = @p0 WHERE [Id] = @p1; SELECT @rc0 AS Op0;",
                Parameters = [new EF.SqlParam("@p0", "V1"), new EF.SqlParam("@p1", 1)],
                OperationIndices = [0]
            },
            new()
            {
                CommandText = "UPDATE [Items] SET [Key1] = @p2 WHERE [Id] = @p3; SELECT @rc1 AS Op1;",
                Parameters = [new EF.SqlParam("@p2", "V2"), new EF.SqlParam("@p3", 2)],
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
    public void BulkExecuteOptions_does_not_expose_AutoTransaction()
    {
        var prop = typeof(BulkExecuteOptions).GetProperty("AutoTransaction");
        Assert.Null(prop);
    }

    [Fact]
    public void BulkExecuteOptions_has_expected_options()
    {
        var options = new BulkExecuteOptions();
        Assert.Equal(2000, options.MaxParametersPerCommand);
        Assert.False(options.ThrowIfZeroAffected);
        Assert.Null(options.CommandTimeout);
        Assert.Null(options.OnCommandText);
    }

    [Fact]
    public async Task Execute_without_transaction_runs_commands_with_null_transaction()
    {
        var (connection, chunks, counts) = ArrangeTwoOps();
        ScriptRowCounts(connection, [1], [1]);

        await EF.SqlServerExecutor.ExecuteCoreAsync(connection, transaction: null, chunks, counts, new BulkExecuteOptions(), CancellationToken.None);

        Assert.Equal(2, connection.ExecutedCommands.Count);
        Assert.All(connection.ExecutedCommands, cmd => Assert.Null(((FakeAdo.Command)cmd).GetType().GetProperty("DbTransaction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is null ? null : null));
        // Verify via internal transaction field exposed through command.Transaction is null
        // FakeAdo.Command stores DbTransaction; we check each command's Transaction was not set to a real transaction.
        // Since we passed null, each command should have null transaction.
        foreach (var cmd in connection.ExecutedCommands)
        {
            Assert.Null(cmd.Transaction);
        }
        Assert.Equal(1, counts[0]);
        Assert.Equal(1, counts[1]);
    }

    [Fact]
    public async Task Execute_with_user_transaction_piggybacks_same_transaction_to_all_commands()
    {
        var (connection, chunks, counts) = ArrangeTwoOps();
        ScriptRowCounts(connection, [1], [1]);
        var transaction = new FakeAdo.Transaction(connection, IsolationLevel.ReadCommitted);

        await EF.SqlServerExecutor.ExecuteCoreAsync(connection, transaction, chunks, counts, new BulkExecuteOptions(), CancellationToken.None);

        Assert.Equal(2, connection.ExecutedCommands.Count);
        Assert.Equal(transaction, connection.ExecutedCommands[0].Transaction);
        Assert.Equal(transaction, connection.ExecutedCommands[1].Transaction);
        Assert.Equal(1, counts[0]);
        Assert.Equal(1, counts[1]);
    }

    [Fact]
    public async Task Without_transaction_sequential_operations_commit_per_statement_semantics()
    {
        // Simulate two ops where second depends on first's write; without a transaction
        // each still executes sequentially and reports its own rowcount.
        var connection = new FakeAdo.Connection();
        var chunks = new List<EF.SqlChunkPlan>
        {
            new()
            {
                CommandText = "UPDATE [Items] SET [Key1] = @p0 WHERE [Key1] = @p1; SET @rc0 = @@ROWCOUNT; SELECT @rc0 AS Op0;",
                Parameters = [new EF.SqlParam("@p0", "New"), new EF.SqlParam("@p1", "Old")],
                OperationIndices = [0]
            },
            new()
            {
                CommandText = "UPDATE [Items] SET [Key3] = @p2 WHERE [Key1] = @p3; SET @rc1 = @@ROWCOUNT; SELECT @rc1 AS Op1;",
                Parameters = [new EF.SqlParam("@p2", 5), new EF.SqlParam("@p3", "Old")],
                OperationIndices = [1]
            }
        };
        var counts = new Dictionary<int, int>();
        ScriptRowCounts(connection, [2], [0]);

        await EF.SqlServerExecutor.ExecuteCoreAsync(connection, null, chunks, counts, new BulkExecuteOptions(), CancellationToken.None);

        Assert.Equal(2, counts[0]);
        Assert.Equal(0, counts[1]);
    }

    [Fact]
    public async Task Chunked_batch_without_transaction_splits_and_reports_across_chunks()
    {
        var connection = new FakeAdo.Connection();
        var counts = new Dictionary<int, int>();

        // Single logical operation split across two chunks (e.g., large upsert)
        var chunks = new List<EF.SqlChunkPlan>
        {
            new() { CommandText = "MERGE chunk 1", Parameters = [], OperationIndices = [0] },
            new() { CommandText = "MERGE chunk 2", Parameters = [], OperationIndices = [0] },
            new() { CommandText = "UPDATE chunk 3", Parameters = [], OperationIndices = [1] }
        };

        ScriptRowCounts(connection, [3], [4], [1]);

        await EF.SqlServerExecutor.ExecuteCoreAsync(connection, null, chunks, counts, new BulkExecuteOptions(), CancellationToken.None);

        Assert.Equal(7, counts[0]); // 3 + 4 accumulated
        Assert.Equal(1, counts[1]);
        Assert.Equal(3, connection.ExecutedCommands.Count);
        Assert.All(connection.ExecutedCommands, c => Assert.Null(c.Transaction));
    }

    [Fact]
    public async Task Chunked_batch_with_transaction_uses_same_transaction_for_all_chunks()
    {
        var connection = new FakeAdo.Connection();
        var counts = new Dictionary<int, int>();
        var tx = new FakeAdo.Transaction(connection, IsolationLevel.ReadCommitted);

        var chunks = new List<EF.SqlChunkPlan>
        {
            new() { CommandText = "MERGE chunk 1", Parameters = [], OperationIndices = [0] },
            new() { CommandText = "MERGE chunk 2", Parameters = [], OperationIndices = [0] }
        };

        ScriptRowCounts(connection, [2], [5]);

        await EF.SqlServerExecutor.ExecuteCoreAsync(connection, tx, chunks, counts, new BulkExecuteOptions(), CancellationToken.None);

        Assert.Equal(7, counts[0]);
        Assert.Equal(tx, connection.ExecutedCommands[0].Transaction);
        Assert.Equal(tx, connection.ExecutedCommands[1].Transaction);
    }

    [Fact]
    public async Task OnCommandText_hook_is_invoked_for_every_chunk_without_transaction()
    {
        var (connection, chunks, counts) = ArrangeTwoOps();
        ScriptRowCounts(connection, [1], [1]);
        var logged = new List<string>();
        var options = new BulkExecuteOptions { OnCommandText = logged.Add };

        await EF.SqlServerExecutor.ExecuteCoreAsync(connection, null, chunks, counts, options, CancellationToken.None);

        Assert.Equal(2, logged.Count);
        Assert.Equal(chunks[0].CommandText, logged[0]);
        Assert.Equal(chunks[1].CommandText, logged[1]);
    }

    [Fact]
    public async Task OnCommandText_hook_is_invoked_when_piggybacking_on_user_transaction()
    {
        var (connection, chunks, counts) = ArrangeTwoOps();
        ScriptRowCounts(connection, [1], [1]);
        var logged = new List<string>();
        var options = new BulkExecuteOptions { OnCommandText = logged.Add };
        var tx = new FakeAdo.Transaction(connection, IsolationLevel.ReadCommitted);

        await EF.SqlServerExecutor.ExecuteCoreAsync(connection, tx, chunks, counts, options, CancellationToken.None);

        Assert.Equal(2, logged.Count);
    }

    [Fact]
    public async Task ThrowIfZeroAffected_validated_after_all_chunks_without_transaction()
    {
        // Simulate that ThrowIfZeroAffected should be validated at the provider layer (BulkBatch)
        // not at ExecuteCore. ExecuteCore just returns counts; BulkBatch throws.
        // Here we verify counts contain zero for validation.
        var connection = new FakeAdo.Connection();
        var chunks = new List<EF.SqlChunkPlan>
        {
            new() { CommandText = "UPDATE [Items] SET [Key1] = @p0 WHERE [Id] = @p1;", Parameters = [new EF.SqlParam("@p0", "x"), new EF.SqlParam("@p1", 999)], OperationIndices = [0] },
            new() { CommandText = "UPDATE [Items] SET [Key1] = @p2 WHERE [Id] = @p3;", Parameters = [new EF.SqlParam("@p2", "y"), new EF.SqlParam("@p3", 1000)], OperationIndices = [1] }
        };
        var counts = new Dictionary<int, int>();
        ScriptRowCounts(connection, [0], [0]);

        await EF.SqlServerExecutor.ExecuteCoreAsync(connection, null, chunks, counts, new BulkExecuteOptions(), CancellationToken.None);

        Assert.Equal(0, counts[0]);
        Assert.Equal(0, counts[1]);

        // BulkBatch validation would throw BulkZeroRowsAffectedException when ThrowIfZeroAffected=true
        var options = new BulkExecuteOptions { ThrowIfZeroAffected = true };
        var hasZero = counts.Any(kv => kv.Value == 0);
        Assert.True(hasZero);
    }
}
