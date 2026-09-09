using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NSLabs.EFCore.Extensions.DependencyInjection;

namespace NSLabs.EFCore.Extensions.Tests.Unit.Sqlite;

[CollectionDefinition("telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection
{
}

[Collection("telemetry")]
public class SqliteTelemetryTests
{
    private sealed class Capture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _stopped = new();

        public Capture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == BulkExecuteTelemetryNames.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            _listener.ActivityStopped += activity =>
            {
                lock (_stopped)
                {
                    _stopped.Add(activity);
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Stopped
        {
            get
            {
                lock (_stopped)
                {
                    return _stopped.ToList();
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private static TestDbContext CreateDatabase(Action<BulkInstrumentationOptions>? configureInstrumentation = null)
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>().UseSqlite("DataSource=:memory:");
        if (configureInstrumentation is not null)
        {
            builder.UseBulkInstrumentation(configureInstrumentation);
        }

        var context = new TestDbContext(builder.Options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private static object? Tag(Activity activity, string key) => activity.GetTagItem(key);

    private static bool HasTag(Activity activity, string key)
        => activity.TagObjects.Any(t => t.Key == key);

    private static Activity FindBatch(Capture capture, int operationCount)
        => capture.Stopped.Single(a =>
            a.OperationName == "BulkExecute" &&
            Equals(a.GetTagItem("nslabs.bulk.operation_count"), operationCount));

    private static IReadOnlyList<Activity> FindChunks(Capture capture, Activity batch)
        => capture.Stopped
            .Where(a => a.OperationName == "BulkExecute.Chunk" && a.TraceId == batch.TraceId)
            .OrderBy(a => (int)a.GetTagItem("nslabs.bulk.chunk.index")!)
            .ToList();

    [Fact]
    public async Task Batch_emits_span_with_counts_and_ok_status()
    {
        using var capture = new Capture();
        using var context = CreateDatabase();
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        context.Items.Add(new Item { Key1 = "b", Key2 = 2 });
        await context.SaveChangesAsync();

        var result = await context.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "a2"));
            b.Update<Item>(op => op.Where(x => x.Id == 2).Set(x => x.Key1, "b2"));
        });

        Assert.Equal(2, result.TotalRowsAffected);

        var batch = FindBatch(capture, operationCount: 2);
        Assert.Equal(ActivityKind.Client, batch.Kind);
        Assert.Equal(ActivityStatusCode.Unset, batch.Status);
        Assert.True(string.IsNullOrEmpty(batch.ParentId));
        Assert.Equal("sqlite", Tag(batch, "db.system"));
        Assert.Equal("bulk_execute", Tag(batch, "db.operation"));
        Assert.Equal(2, Tag(batch, "nslabs.bulk.update_count"));
        Assert.Equal(0, Tag(batch, "nslabs.bulk.upsert_count"));
        Assert.Equal(0, Tag(batch, "nslabs.bulk.delete_count"));
        Assert.Equal(2, Tag(batch, "nslabs.bulk.chunk_count"));
        Assert.Equal(2, Tag(batch, "nslabs.bulk.total_rows"));
        Assert.False(HasTag(batch, "db.statement"));

        // SQLite emits one chunk per update: exactly 2 child spans.
        var chunks = FindChunks(capture, batch);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c =>
        {
            Assert.Equal(ActivityKind.Client, c.Kind);
            Assert.Equal(ActivityStatusCode.Unset, c.Status);
            Assert.Equal(batch.SpanId, c.ParentSpanId);
            Assert.Equal("sqlite", c.GetTagItem("db.system"));
            Assert.Equal(1, c.GetTagItem("nslabs.bulk.chunk.rows"));
            Assert.False(HasTag(c, "db.statement"));
        });
        Assert.Equal(0, chunks[0].GetTagItem("nslabs.bulk.chunk.index"));
        Assert.Equal(1, chunks[1].GetTagItem("nslabs.bulk.chunk.index"));
    }

    [Fact]
    public async Task Child_spans_nest_under_ambient_parent()
    {
        using var capture = new Capture();
        using var context = CreateDatabase();
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync();

        using var parent = new Activity("telemetry-test-parent").Start();

        await context.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "a2"));
        });

        parent.Stop();

        var batch = FindBatch(capture, operationCount: 1);
        Assert.Equal(parent.Id, batch.ParentId);
        var chunks = FindChunks(capture, batch);
        Assert.Single(chunks);
        Assert.Equal(batch.SpanId, chunks[0].ParentSpanId);
    }

    [Fact]
    public async Task No_listener_no_span_no_throw()
    {
        // No Capture registered: proves the zero-overhead path executes normally.
        using var context = CreateDatabase();
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync();

        var result = await context.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "a2"));
        });

        Assert.Equal(1, result.TotalRowsAffected);
        // Bulk execution bypasses change tracking: re-read without tracking.
        Assert.Equal("a2", (await context.Items.AsNoTracking().SingleAsync(x => x.Id == 1)).Key1);
    }

    [Fact]
    public async Task Empty_batch_emits_no_span()
    {
        using var capture = new Capture();
        using var context = CreateDatabase();

        var result = await context.BulkExecuteAsync(_ => { });

        Assert.Equal(BulkExecuteResult.Empty, result);
        Assert.Empty(capture.Stopped);
    }

    [Fact]
    public async Task Error_sets_error_status_and_records_exception()
    {
        using var capture = new Capture();
        using var context = CreateDatabase();
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BulkZeroRowsAffectedException>(() => context.BulkExecuteAsync(
            b => b.Update<Item>(op => op.Where(x => x.Id == 999).Set(x => x.Key1, "x")),
            new BulkExecuteOptions { ThrowIfZeroAffected = true }));

        Assert.Equal(0, ex.OperationIndex);

        var batch = FindBatch(capture, operationCount: 1);
        Assert.Equal(ActivityStatusCode.Error, batch.Status);
        Assert.Equal(typeof(BulkZeroRowsAffectedException).FullName, Tag(batch, "error.type"));
        var exceptionEvent = Assert.Single(batch.Events, e => e.Name == "exception");
        Assert.Equal(typeof(BulkZeroRowsAffectedException).FullName, exceptionEvent.Tags.Single(t => t.Key == "exception.type").Value);
    }

    [Fact]
    public async Task RecordException_false_suppresses_event_but_keeps_error_status()
    {
        using var capture = new Capture();
        using var context = CreateDatabase(o => o.RecordException = false);
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<BulkZeroRowsAffectedException>(() => context.BulkExecuteAsync(
            b => b.Update<Item>(op => op.Where(x => x.Id == 999).Set(x => x.Key1, "x")),
            new BulkExecuteOptions { ThrowIfZeroAffected = true }));

        var batch = FindBatch(capture, operationCount: 1);
        Assert.Equal(ActivityStatusCode.Error, batch.Status);
        Assert.Equal(typeof(BulkZeroRowsAffectedException).FullName, Tag(batch, "error.type"));
        Assert.DoesNotContain(batch.Events, e => e.Name == "exception");
    }

    [Fact]
    public async Task Chunk_spans_disabled_by_option()
    {
        using var capture = new Capture();
        using var context = CreateDatabase(o => o.EnableChunkSpans = false);
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        context.Items.Add(new Item { Key1 = "b", Key2 = 2 });
        await context.SaveChangesAsync();

        var result = await context.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "a2"));
            b.Update<Item>(op => op.Where(x => x.Id == 2).Set(x => x.Key1, "b2"));
        });

        Assert.Equal(2, result.TotalRowsAffected);
        var batch = FindBatch(capture, operationCount: 2);
        Assert.Equal(ActivityStatusCode.Unset, batch.Status);
        Assert.Empty(FindChunks(capture, batch));
    }

    [Fact]
    public async Task CommandText_never_captured_by_default()
    {
        using var capture = new Capture();
        using var context = CreateDatabase();
        context.Items.Add(new Item { Key1 = "secret-value", Key2 = 1 });
        await context.SaveChangesAsync();

        await context.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "other"));
        });

        var batch = FindBatch(capture, operationCount: 1);
        Assert.False(HasTag(batch, "db.statement"));
        Assert.False(HasTag(batch, "nslabs.bulk.command_truncated"));
        var chunk = Assert.Single(FindChunks(capture, batch));
        Assert.False(HasTag(chunk, "db.statement"));
    }

    [Fact]
    public async Task CaptureCommandText_opt_in_truncates()
    {
        using var capture = new Capture();
        using var context = CreateDatabase(o =>
        {
            o.CaptureCommandText = true;
            o.MaxCommandLength = 20;
        });
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync();

        await context.BulkExecuteAsync(b =>
        {
            b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "b"));
        });

        var batch = FindBatch(capture, operationCount: 1);
        var statement = Assert.IsType<string>(Tag(batch, "db.statement"));
        Assert.True(statement.Length <= 20);
        Assert.Equal(true, Tag(batch, "nslabs.bulk.command_truncated"));
        var chunk = Assert.Single(FindChunks(capture, batch));
        Assert.IsType<string>(Tag(chunk, "db.statement"));
    }

    [Fact]
    public async Task Explicit_BulkExecuteOptions_does_not_reset_instrumentation()
    {
        using var capture = new Capture();
        using var context = CreateDatabase(o => o.CaptureCommandText = true);
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync();

        // Explicit execution options replace execution config only;
        // instrumentation (CaptureCommandText) still resolves from the context.
        await context.BulkExecuteAsync(
            b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "b")),
            new BulkExecuteOptions());

        var batch = FindBatch(capture, operationCount: 1);
        Assert.IsType<string>(Tag(batch, "db.statement"));
    }

    [Fact]
    public async Task Cancellation_marks_span_error()
    {
        using var capture = new Capture();
        using var context = CreateDatabase();
        context.Items.Add(new Item { Key1 = "a", Key2 = 1 });
        await context.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.BulkExecuteAsync(
            b => b.Update<Item>(op => op.Where(x => x.Id == 1).Set(x => x.Key1, "b")),
            cts.Token));

        var batch = FindBatch(capture, operationCount: 1);
        Assert.Equal(ActivityStatusCode.Error, batch.Status);
        Assert.NotNull(Tag(batch, "error.type"));
    }

    [Fact]
    public void Instrumentation_factory_defaults_are_safe()
    {
        var options = new BulkInstrumentationOptions();
        Assert.True(options.EnableChunkSpans);
        Assert.True(options.RecordException);
        Assert.False(options.CaptureCommandText);
        Assert.Equal(4000, options.MaxCommandLength);
    }

    [Fact]
    public void Instrumentation_clone_is_independent_of_source()
    {
        var source = new BulkInstrumentationOptions
        {
            EnableChunkSpans = false,
            RecordException = false,
            CaptureCommandText = true,
            MaxCommandLength = 100,
        };
        var clone = source.Clone();

        clone.EnableChunkSpans = true;
        clone.RecordException = true;
        clone.CaptureCommandText = false;
        clone.MaxCommandLength = 4000;

        Assert.False(source.EnableChunkSpans);
        Assert.False(source.RecordException);
        Assert.True(source.CaptureCommandText);
        Assert.Equal(100, source.MaxCommandLength);
    }

    [Fact]
    public void UseBulkInstrumentation_is_additive_and_validated()
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>().UseSqlite("DataSource=:memory:");
        builder.UseBulkInstrumentation(o => o.CaptureCommandText = true);
        builder.UseBulkInstrumentation(o => o.MaxCommandLength = 100);

        var snapshot = builder.Options.FindExtension<BulkInstrumentationOptionsExtension>()
            ?? throw new InvalidOperationException("Expected BulkInstrumentationOptionsExtension to be present.");
        Assert.True(snapshot.Options.CaptureCommandText);
        Assert.Equal(100, snapshot.Options.MaxCommandLength);

        var bad = new DbContextOptionsBuilder<TestDbContext>().UseSqlite("DataSource=:memory:");
        Assert.Throws<ArgumentOutOfRangeException>(() => bad.UseBulkInstrumentation(o => o.MaxCommandLength = 0));
    }

    [Fact]
    public void BulkExecuteOptions_untouched_by_instrumentation()
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>().UseSqlite("DataSource=:memory:");
        builder.UseBulkInstrumentation(o => o.CaptureCommandText = true);

        // Execution extension is independent: absent unless configured.
        Assert.Null(builder.Options.FindExtension<BulkExecuteOptionsExtension>());
    }
}
