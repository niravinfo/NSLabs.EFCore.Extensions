using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSLabs.EFCore.Extensions.Diagnostics;

namespace NSLabs.EFCore.Extensions.Tests.Unit.Sqlite;

// Serialized with telemetry tests: both execute real batches, and an ActivityListener
// from a parallel telemetry test would otherwise capture our spans (and vice versa).
[Collection("telemetry")]
public sealed class BulkExecuteLoggingTests
{
    private sealed record LogEntry(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> State);

    private sealed class ListProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _sink;
        private readonly Func<string, LogLevel, bool> _isEnabled;

        public ListProvider(List<LogEntry> sink, Func<string, LogLevel, bool> isEnabled)
        {
            _sink = sink;
            _isEnabled = isEnabled;
        }

        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, _sink, _isEnabled);

        public void Dispose()
        {
        }
    }

    private sealed class ListLogger : ILogger
    {
        private readonly string _category;
        private readonly List<LogEntry> _sink;
        private readonly Func<string, LogLevel, bool> _isEnabled;

        public ListLogger(string category, List<LogEntry> sink, Func<string, LogLevel, bool> isEnabled)
        {
            _category = category;
            _sink = sink;
            _isEnabled = isEnabled;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => _isEnabled(_category, logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var values = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key != "{OriginalFormat}")
                    {
                        values[pair.Key] = pair.Value;
                    }
                }
            }

            lock (_sink)
            {
                _sink.Add(new LogEntry(_category, logLevel, eventId, formatter(state, exception), exception, values));
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private static TestDbContext CreateDatabase(
        List<LogEntry> sink,
        Func<string, LogLevel, bool>? isEnabled = null)
    {
        // LoggerFactory.Create + AddProvider is the supported MEL wiring
        // (ILoggerFactory has no instance AddProvider API).
        var factory = LoggerFactory.Create(builder =>
        {
            // Trace minimum at the factory: level filtering is exercised solely by the
            // test's IsEnabled func (otherwise the factory would swallow Debug itself).
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new ListProvider(sink, isEnabled ?? ((_, _) => true)));
        });

        var builder = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite("DataSource=:memory:")
            .UseLoggerFactory(factory);
        var context = new TestDbContext(builder.Options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private static IReadOnlyList<LogEntry> Ours(List<LogEntry> sink)
    {
        lock (sink)
        {
            return sink.Where(e => e.Category == BulkLoggerCategory.Name).ToList();
        }
    }

    [Fact]
    public void Category_and_event_ids_are_stable()
    {
        Assert.Equal("NSLabs.EFCore.Extensions", BulkLoggerCategory.Name);
        Assert.Equal(BulkExecuteTelemetryNames.SourceName, BulkLoggerCategory.Name);
        Assert.Equal(60000, BulkEventId.BaseId);
        Assert.Equal(60000, BulkEventId.BulkExecuteStartingId);
        Assert.Equal(60001, BulkEventId.BulkExecuteExecutedId);
        Assert.Equal(60004, BulkEventId.BulkExecuteFailedId);
        Assert.Equal(BulkEventId.BulkExecuteStartingId, BulkEventId.BulkExecuteStarting.Id);
        Assert.Equal(nameof(BulkEventId.BulkExecuteStarting), BulkEventId.BulkExecuteStarting.Name);
        Assert.Equal(BulkEventId.BulkExecuteExecutedId, BulkEventId.BulkExecuteExecuted.Id);
        Assert.Equal(BulkEventId.BulkExecuteFailedId, BulkEventId.BulkExecuteFailed.Id);
    }

    [Fact]
    public async Task Success_logs_starting_at_Debug_and_executed_at_Information()
    {
        var sink = new List<LogEntry>();
        using var context = CreateDatabase(sink);
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await context.BulkExecuteAsync(
            b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "a2")),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalRowsAffected);

        var ours = Ours(sink);
        var starting = Assert.Single(ours, e => e.EventId.Id == BulkEventId.BulkExecuteStartingId);
        Assert.Equal(LogLevel.Debug, starting.Level);
        Assert.Equal(nameof(BulkEventId.BulkExecuteStarting), starting.EventId.Name);

        var executed = Assert.Single(ours, e => e.EventId.Id == BulkEventId.BulkExecuteExecutedId);
        Assert.Equal(LogLevel.Information, executed.Level);
        Assert.Equal(nameof(BulkEventId.BulkExecuteExecuted), executed.EventId.Name);
        Assert.Contains("1 row(s)", executed.Message);

        // Structured params per the C.3 catalog (placeholders, never values).
        Assert.Equal(1, starting.State["OperationCount"]);
        Assert.Equal(1, starting.State["UpdateCount"]);
        Assert.DoesNotContain("a2", starting.Message);
        Assert.DoesNotContain("a2", executed.Message);
    }

    [Fact]
    public async Task Information_minimum_hides_starting_but_keeps_executed()
    {
        var sink = new List<LogEntry>();
        using var context = CreateDatabase(sink, (_, level) => level >= LogLevel.Information);
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await context.BulkExecuteAsync(
            b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "a2")),
            TestContext.Current.CancellationToken);

        var ours = Ours(sink);
        Assert.DoesNotContain(ours, e => e.EventId.Id == BulkEventId.BulkExecuteStartingId);
        Assert.Single(ours, e => e.EventId.Id == BulkEventId.BulkExecuteExecutedId);
    }

    [Fact]
    public async Task Failure_logs_error_with_exception_and_no_executed()
    {
        var sink = new List<LogEntry>();
        using var context = CreateDatabase(sink);
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<BulkZeroRowsAffectedException>(() => context.BulkExecuteAsync(
            b => b.Update<Item>(op => op.Where(x => x.Id == 999).Set(x => x.Key1, "x")),
            new BulkExecuteOptions { ThrowIfZeroAffected = true },
            TestContext.Current.CancellationToken));

        var ours = Ours(sink);
        Assert.Single(ours, e => e.EventId.Id == BulkEventId.BulkExecuteStartingId);
        var failed = Assert.Single(ours, e => e.EventId.Id == BulkEventId.BulkExecuteFailedId);
        Assert.Equal(LogLevel.Error, failed.Level);
        Assert.Equal(nameof(BulkEventId.BulkExecuteFailed), failed.EventId.Name);
        Assert.IsType<BulkZeroRowsAffectedException>(failed.Exception);
        Assert.DoesNotContain(ours, e => e.EventId.Id == BulkEventId.BulkExecuteExecutedId);

        // The chunk itself executed (0 rows matched); the throw happens afterwards
        // in ThrowIfZeroAffected validation — so 60002/60003 precede 60004.
        Assert.Single(ours, e => e.EventId.Id == BulkEventId.BulkChunkExecutingId);
        var chunkExecuted = Assert.Single(ours, e => e.EventId.Id == BulkEventId.BulkChunkExecutedId);
        Assert.Equal(0, chunkExecuted.State["RowsAffected"]);
    }

    [Fact]
    public async Task Chunk_logs_include_sql_text_but_never_values()
    {
        var sink = new List<LogEntry>();
        using var context = CreateDatabase(sink);
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await context.BulkExecuteAsync(
            b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "secret-value")),
            TestContext.Current.CancellationToken);

        var ours = Ours(sink);
        var executing = Assert.Single(ours, e => e.EventId.Id == BulkEventId.BulkChunkExecutingId);
        Assert.Equal(LogLevel.Information, executing.Level);
        Assert.Equal(nameof(BulkEventId.BulkChunkExecuting), executing.EventId.Name);
        Assert.Equal(0, executing.State["ChunkIndex"]);
        Assert.Equal(1, executing.State["OperationCount"]);

        // SQL shape is always logged (placeholders-only by construction)...
        var sql = Assert.IsType<string>(executing.State["CommandText"]);
        Assert.Contains("UPDATE", sql);
        Assert.Contains("@p", sql);

        // ...but values never appear in any message or structured state.
        Assert.DoesNotContain("secret-value", executing.Message);
        Assert.DoesNotContain("secret-value", sql);
        Assert.All(ours, e => Assert.DoesNotContain("secret-value", e.Message));

        var executed = Assert.Single(ours, e => e.EventId.Id == BulkEventId.BulkChunkExecutedId);
        Assert.Equal(LogLevel.Information, executed.Level);
        Assert.Equal(0, executed.State["ChunkIndex"]);
        Assert.Equal(1, executed.State["RowsAffected"]);
    }

    [Fact]
    public async Task Each_chunk_logs_its_own_indexed_pair()
    {
        var sink = new List<LogEntry>();
        using var context = CreateDatabase(sink);
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        context.Items.Add(new Item { Key1 = "b", Key2 = 2 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // SQLite emits one chunk per update: 2 ops → 2 chunks.
        await context.BulkExecuteAsync(
            b =>
            {
                b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "a2"));
                b.Update<Item>(op => op.Where(x => x.Id == 2).Set(x => x.Key1, "b2"));
            },
            TestContext.Current.CancellationToken);

        var ours = Ours(sink);
        var executing = ours.Where(e => e.EventId.Id == BulkEventId.BulkChunkExecutingId).ToList();
        var executed = ours.Where(e => e.EventId.Id == BulkEventId.BulkChunkExecutedId).ToList();
        Assert.Equal(2, executing.Count);
        Assert.Equal(2, executed.Count);
        Assert.Equal([0, 1], executing.Select(e => e.State["ChunkIndex"]).ToList());
        Assert.Equal([0, 1], executed.Select(e => e.State["ChunkIndex"]).ToList());
    }

    [Fact]
    public async Task No_logger_factory_executes_without_throwing()
    {
        using var context = SqliteHarness.CreateContext();
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await context.BulkExecuteAsync(
            b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "a2")),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalRowsAffected);
        Assert.Equal("a2", (await context.Items.AsNoTracking().SingleAsync(
            x => x.Id == 1, TestContext.Current.CancellationToken)).Key1);
    }

    [Fact]
    public async Task Empty_batch_emits_no_logs()
    {
        var sink = new List<LogEntry>();
        using var context = CreateDatabase(sink);

        var result = await context.BulkExecuteAsync(_ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(BulkExecuteResult.Empty, result);
        Assert.Empty(Ours(sink));
    }
}
