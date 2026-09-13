# Logging Plan — NSLabs.EFCore.Extensions

**Goal:** give the library first-class `Microsoft.Extensions.Logging` (MEL) support the way
EF Core itself does it: zero-config under the user's existing `LogTo` / `ILoggerFactory`
setup, structured and filterable, privacy-safe by default, allocation-free when disabled.

**Non-goal:** replacing the existing `ActivitySource` tracing (`BulkExecute` /
`BulkExecute.Chunk` spans stay as-is; logs and traces correlate instead of duplicating).

> All EF Core mechanics below were verified against the EF Core 10.0.12 package
> (`CoreOptionsExtension.IsSensitiveDataLoggingEnabled`,
> `ILoggingOptions.IsSensitiveDataLoggingEnabled` are public API) and the efcore
> source (`LoggerCategory<T>`, `IDiagnosticsLogger`, `LoggingDefinitions`,
> `CoreEventId`: `CoreBaseId = 10000`, `RelationalBaseId = 20000`,
> `ProviderBaseId = 30000`, `ProviderDesignBaseId = 35000`).

---

## Part A — How EF Core does logging

### A.1 The five mechanisms (and which one is "logging")

EF Core's own guidance ([overview of logging and interception](https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/)):

| Mechanism | Intended use |
|---|---|
| Simple logging (`LogTo`) | Development-time logging |
| `Microsoft.Extensions.Logging` | **Production logging** |
| .NET events | Reacting to EF events (sync only) |
| Interceptors | **Manipulating** operations (can change/suppress behavior) |
| Diagnostic listeners | Process-wide application diagnostics |

Rule of thumb, stated explicitly by EF: *interceptors are different from logging —
`LogTo` / MEL are the better choice for logging.* Our library therefore logs through
MEL and does **not** try to fake interceptor or `DiagnosticSource` events for its
commands (which bypass EF's pipeline — see `DESIGN.md` §3).

### A.2 Configuration surface (what users already know)

- `optionsBuilder.UseLoggerFactory(factory)` / `AddDbContext` (picks up the app's factory
  automatically in ASP.NET / Generic Host) / `optionsBuilder.LogTo(...)`.
- `EnableSensitiveDataLogging()` — data values may appear in logs/exceptions; off by default.
- `EnableDetailedErrors()` — richer error details at a perf cost.
- `ConfigureWarnings(w => w.Log(...)/.Ignore(...)/.Throw(...))` — per-event level/behavior.

**Consequence for us:** if we resolve our `ILogger` from the context's internal service
provider, *all of the above keep working with zero new concepts* — category filtering in
`LogTo`, provider routing (Console/Serilog/OTel bridge), level filtering.

### A.3 Categories and event IDs

- Categories are stable strings: `Microsoft.EntityFrameworkCore.Database.Command`,
  `...Update`, `...Query`, `...Model`, `...Infrastructure`, ... exposed for discovery via
  the `DbLoggerCategory` nested types (`LoggerCategory<T>.Name`).
- Events are `EventId` (id + name) fields on `CoreEventId` (all providers),
  `RelationalEventId` (relational), `SqlServerEventId`, etc. IDs are stable across
  releases ("must not change between releases") and partitioned by base:
  core ≥ 10000, relational ≥ 20000, providers ≥ 30000, provider design-time ≥ 35000.
- `ConfigureWarnings` keys off these IDs.

**Consequence for us:** use our *own* category (`Microsoft.EntityFrameworkCore.*` is
EF-owned; filtering by EF categories must never accidentally capture us and vice versa)
and our *own* ID range **≥ 60000** — clear of core, relational, provider,
and provider-design bands.

### A.4 Message implementation

- Modern EF generates messages with the `[LoggerMessage]` source generator
  (`*LoggingDefinitions` partial classes): no boxing, no parsing at runtime, trim/AOT-safe.
- Hot paths are guarded by `IsEnabled(level)` *before* building arguments.
- Two message variants exist wherever data is involved: plain vs. sensitive.
- `ILoggingOptions.IsSensitiveDataLoggingEnabled` is public; options-level state is readable
  via `CoreOptionsExtension.IsSensitiveDataLoggingEnabled` without resolving services.

### A.5 The dual pipeline we deliberately do NOT plug into

`IDiagnosticsLogger<T>` fuses `ILogger` + `DiagnosticSource` + `EventData` payloads, cached
per-event in `LoggingDefinitions`. But `LoggingDefinitions` is documented as
*"public so that it can be inherited by database providers… it should not be used for
any other purpose"*, and its members are `[EntityFrameworkInternal]`.
We are an **extension library, not a provider** → plain MEL `ILogger`, resolved like any
consumer:

```csharp
var logger = context.GetService<ILoggerFactory>()?.CreateLogger(BulkLoggerCategory.Name);
```

(`GetService<T>()` is the existing resolution pattern already used in `BulkBatch`;
the factory is app-configured, so `LogTo` filters/levels apply automatically. When the
app configures no logging, the factory yields no-op loggers and `IsEnabled` is false —
zero-cost.)

### A.6 Options debuggability (already done here)

`IDbContextOptionsExtension` contributes `Info.LogFragment` and
`PopulateDebugInfo()` — `BulkExecuteOptionsExtension` already implements both.
New logging options must extend both (see C.6).

---

## Part B — Current state of this library

| Piece | File | Status |
|---|---|---|
| `ActivitySource` spans (`BulkExecute`, `BulkExecute.Chunk`), OTel semconv tags, truncation policy | `Diagnostics/BulkExecuteTelemetry.cs`, `BulkExecuteTelemetryNames.cs` | Done, tested |
| Process-wide policy (`BulkInstrumentation` / `BulkInstrumentationOptions`: chunk spans, `CaptureCommandText`, `MaxCommandLength`, `RecordException`) | `BulkInstrumentation*.cs`, `DependencyInjection/BulkInstrumentationServiceExtensions.cs` | Done |
| Per-context execution options (`BulkExecuteOptionsExtension : IDbContextOptionsExtension` + `LogFragment`/`PopulateDebugInfo`) | `DependencyInjection/BulkExecuteOptionsExtension.cs` | Done |
| Execution over `GetDbConnection()` + raw `DbCommand` → EF command logging/interceptors do **not** fire | `DESIGN.md` §3 | Known, documented |
| Minimal MEL stub: 3 events in `ExecuteCoreAsync` | `BulkBatch.cs` (`1001/BulkExecuteStart`, `1002/BulkExecuteCompleted` @Debug, `1101/BulkExecuteFailed` @Error) | **Needs hardening — gaps below** |

### Gaps in the current stub (what this plan fixes)

1. **Category** is `typeof(BulkBatch).FullName` — an implementation detail, not a stable,
   documented, filter-friendly category.
2. **Event IDs** (1001/1002/1101) are ad hoc, undocumented, and lack a reserved range.
3. **Levels** don't match EF parity: completion is `Debug`, so a standard EF logging setup
   (commands at `Information`) never shows a completed batch. No per-chunk or retry events.
4. **No duration** is captured or logged (EF logs elapsed ms on every command).
5. **No scopes** — no batch correlation ID, no trace/span linkage to our own Activities.
6. **No sensitive-data policy** — SQL text is never logged today (safe), but there is no
   defined rule for when it *may* be (cf. `CaptureCommandText` precedent in telemetry).
7. **Non-source-generated** `LogDebug/LogError` calls (boxing, parsing, trim-hostile);
   industry standard is `[LoggerMessage]`.
8. Logger resolution is wrapped in swallow-all `try/catch` with the policy rationale
   undocumented; `ResolveInstrumentationEffective()` is computed under a vague
   "tracing or debug" condition rather than per-event need.

---

## Part C — Proposal

### C.1 Principles

1. **Ambient, zero-config.** Logging flows through the context's `ILoggerFactory`.
   A user with `LogTo` configured sees our events with no library-specific setup.
2. **EF parity where EF owns the concept; own identity where it doesn't.**
   Levels mirror `Database.Command` (start ~ `Debug`, completion ~ `Information`,
   failure ~ `Error`, retry ~ `Warning`); category and ID range are ours.
3. **Privacy by default.** Counts, names, durations, shapes — always. SQL text — only
   under the policy in C.4. Parameter *values* — never (explicit deviation from EF,
   justified: values add breach surface for marginal debug value; shapes + counts
   diagnose batching issues).
4. **Pay-for-play performance.** `IsEnabled` guard per event, `[LoggerMessage]`
   source-gen, no arg evaluation when disabled, no logging on the validation-only path.
5. **Never break execution for observability** (existing house rule): logging code is
   side-effect free; per MEL contract loggers don't throw, so no `try/catch` around
   `ILogger` calls — but keep the defensive resolve (factory lookup may fail on exotic
   hosts) and document it instead of swallowing silently.
6. **Additive and non-breaking.** Existing event names/IDs are young and undocumented;
   replace them with the catalog in C.3 (note the rename in release notes). Public API
   additions only thereafter; new events append IDs, never reuse.

### C.2 Category

```csharp
// Diagnostics/BulkLoggerCategory.cs
public static class BulkLoggerCategory
{
    /// <summary>Logger category for all bulk-execution events. Filter with
    /// <c>optionsBuilder.LogTo(..., new[] { BulkLoggerCategory.Name })</c>.</summary>
    public const string Name = "NSLabs.EFCore.Extensions";
}
```

Identical to `BulkExecuteTelemetryNames.SourceName` on purpose: one identity across
logs, traces, and `LogTo` filters. (A single shared const is even better — decide in
implementation whether `BulkExecuteTelemetryNames` absorbs it.)

### C.3 Event catalog (all in `BulkLoggerCategory.Name`, IDs ≥ 60000, stable forever)

| ID | Name | Level | When | Structured params |
|---|---|---|---|---|
| 60000 | `BulkExecuteStarting` | Debug | Batch validated, chunks planned, before first round-trip | OperationCount, UpdateCount, UpsertCount, DeleteCount, ChunkCount, Provider, Database |
| 60001 | `BulkExecuteExecuted` | Information | Batch completed | OperationCount, TotalRows, ElapsedMs, ChunkCount, Provider, Database |
| 60002 | `BulkChunkExecuting` | Information | Before each chunk command | ChunkIndex, OperationCount, ParameterCount, CommandText (always, placeholders-only) |
| 60003 | `BulkChunkExecuted` | Information | After each chunk | ChunkIndex, RowsAffected, ElapsedMs |
| 60004 | `BulkExecuteFailed` | Error | Exception escapes (incl. `BulkZeroRowsAffectedException`); exception passed as such | OperationCount, Provider + `Exception` |
| 60005 | `BulkExecuteRetrying` | Warning | Execution-strategy retry attempt (EF parity: `ExecutionStrategyRetrying` is Warning) | Attempt, ElapsedMs, exception message (no values) |

Notes:

- 60000 replaces stub `1001`, 60001 replaces `1002` (promoted Debug → Information —
  this is the headline behavior change: completions become visible under default EF
  logging), 60004 replaces `1101`.
- Message templates are fixed English sentences with `{PascalCase}` placeholders
  (EF style); no string interpolation, no concatenation.
- Elapsed time via a single `Stopwatch` started in `ExecuteCoreAsync` (batch) and
  per-chunk measurements around each command execution.

### C.4 Sensitive-data policy (EF parity: SQL always, values never)

EF Core logs SQL command text unconditionally at Information (placeholders only);
`EnableSensitiveDataLogging()` adds parameter *values*. Our generators emit
placeholders-only SQL by construction (all values travel in the separate `SqlParam`
list, never interpolated — see `DESIGN.md` §2), so the same rule applies with no
per-call flag check:

| Content | Rule |
|---|---|
| Counts, table/entity names, provider, db name, durations, chunk shapes | Always logged |
| SQL command text (`chunk.CommandText`: placeholders, zero values possible) | Always logged |
| Parameter values | **Never**, even with sensitive logging (C.1.3) — no values parameter exists on any log method |

`MaxCommandLength` truncation (existing helper `BulkExecuteTelemetry.Truncate`, fixed
`4000` matching the instrumentation default) applies identically; log a
`CommandTruncated=true` property when truncated. Telemetry `CaptureCommandText` stays
separate and unchanged. There is deliberately **no** `ShouldLogCommandText` helper
and no `CoreOptionsExtension` read anywhere in the logging path.

### C.5 Configuration surface (no new API — follow EF)

Logging is **ambient, not per-call**: no parameters on `BulkExecuteOptions`, no arguments
on `ExecuteAsync`, and no `UseBulkLogging` extension. Rationale: EF configures logging
on the context (`LogTo`, `UseLoggerFactory`, `EnableSensitiveDataLogging()`,
`ConfigureWarnings`), never per `SaveChanges`; a library-specific knob would fight MEL
filters, add a second source of truth for privacy, and complicate the hot path.

- Basic logs (counts, durations, errors): work with whatever logging the app already
  configured. Nothing to add next to `UseBulkExecute(...)`.
- SQL text: gated solely by EF's `EnableSensitiveDataLogging()`. Off by default,
  on when the user opts in — same rule as EF's own `Database.Command` logs.
- No new `IDbContextOptionsExtension`, no `LogFragment`/`PopulateDebugInfo` changes.

### C.6 Implementation sketch

New/changed files (all under `src/NSLabs.EFCore.Extensions/`):

1. `Diagnostics/BulkLoggerCategory.cs` — the `Name` const (C.2).
2. `Diagnostics/BulkEventId.cs` — `public static class` with `EventId` fields
   (`new EventId(60000, nameof(...))`) + `BaseId = 60000` const; XML docs per event.
3. `Diagnostics/BulkExecuteLoggingDefinitions.cs` — `internal static partial class` with
   one `[LoggerMessage(EventId = ..., Level = ..., Message = "...")] partial` method per
   catalog row (max ~6 params each to stay in the generator's fast path).
4. `BulkBatch.ExecuteCoreAsync` — resolve `ILogger` once (after the
   empty-batch early-out, before chunk generation); `Stopwatch` for batch; emit
   60000 → 60001 / catch → 60004. Guard every call with
   `bulkLogger.IsEnabled(...)`; all argument building stays inside the guards.
5. Executors (`SqlServer/Sqlite/NpgsqlExecutor.RunAsync`) — resolve the bulk logger
   once via `BulkExecuteLogging.ResolveBulkLogger(context)` (level-agnostic: null
   means "no factory", never "disabled"); thread `TelemetryContext?` + `ILogger?
   bulkLogger` through `ExecuteCoreAsync` → `ExecuteChunkAsync` (`CancellationToken`
   last). Each chunk event checks its own level (`isChunkLoggingEnabled`) with a
   per-chunk `Stopwatch`; SQL text passes through unconditionally (C.4).
6. `Diagnostics/BulkExecuteLogging.cs` (internal) — `ResolveBulkLogger(DbContext)`;
   future home of `BeginBatchScope(ILogger, batchId, provider, operationCount,
   activity)` stamping `TraceId`/`SpanId` from `Activity.Current` when present
   (log↔trace correlation, Phase 2 remainder).
7. Tests (`tests/...Unit.Shared` or per-provider unit suites): in-memory
   `ILoggerProvider` harness asserting — catalog IDs/levels/category; completion at
   Information; chunk events at Information with SQL text always present
   (placeholders-only); values never present; no-throw when no factory;
   truncation flag; Warning/Error-only minimum hides Information chunk events.
   Mirror the existing `SqliteTelemetryTests` style (shared non-parallel
   collection so parallel telemetry listeners can't cross-capture spans).
8. Docs: README "Logging" section (`LogTo` filter-by-category example, sensitive-data
   rule, event table pointer), XML docs on all new public API, release-notes entry for
   the 1001/1002/1101 → 60000/60001/60004 rename.

Explicit non-goals: firing `IDbCommandInterceptor` events for our commands (would be
dishonest — interceptors can mutate/suppress; we own our commands); `DiagnosticSource`
`EventData` payloads (provider-plumbing, §A.5); per-call log verbosity; logging in the
model-binding/validation path (validation throws rich exceptions already — exceptions
are the signal there).

### C.7 OpenTelemetry interplay (no new OTel work)

- Keep emitting exactly the spans/tags of today; add nothing OTel-specific for logging.
- Users get logs into OTel via the standard MEL bridge
  (`ILoggingBuilder.AddOpenTelemetry()`, same `OpenTelemetry.Extensions.Hosting`
  package the samples already use) — no `OpenTelemetry.*` reference from `src`,
  preserving the inbox-only posture.
- The `TraceId`/`SpanId` scope properties (C.6) make log records joinable to our
  `BulkExecute` spans in any backend.

---

## Part D — Phased breakdown

| Phase | Scope | Acceptance |
|---|---|---|
| 1 — Core events | Category + EventIds + `[LoggerMessage]` definitions; wire 60000/60001/60004 + `Stopwatch` into `ExecuteCoreAsync` | Unit tests: IDs/levels/category, Info completion, Error with exception, no-throw unconfigured |
| 2 — Detail | 60002/60003 per-chunk at Information with unconditional placeholders-only SQL; level-agnostic `ResolveBulkLogger` + per-site `IsEnabled`; `TelemetryContext` threading (`CancellationToken` last); truncation flag; 60005 at retry site; batch scope with trace correlation | Golden-log tests: SQL always present, values never, Warning-minimum hides Info events, truncation |
| 3 — Docs & samples | README section, XML docs, release note; extend one sample (Sqlite + `NSLABS_OTEL_CONSOLE`) to show logs flowing to console/OTel | Sample run output reviewed; docs build clean |

---

## Part E — Decisions needed from maintainers

1. **Category string**: `NSLabs.EFCore.Extensions` (recommended, = ActivitySource name) vs.
   something hierarchical like `NSLabs.EFCore.Extensions.BulkExecute`?
2. **Parameter values**: settled: never-log (only deviation from EF). SQL text needs
   no flag read at all — placeholders-only by construction (C.4). No `UseBulkLogging`
   override (C.5).
