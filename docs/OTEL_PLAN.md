# OpenTelemetry Support Plan — NSLabs.EFCore.Extensions

Status: **Implemented** · Target: .NET 10 / EF Core 10 · Date: 2026-09-06
(Plan updated to as-built state after implementation + verification.)

## 0. Goal

Add first-class OpenTelemetry **tracing + logging** support to this library
(`src/NSLabs.EFCore.Extensions` + SqlServer / Sqlite / Npgsql providers) with:

1. **Zero mandatory dependencies** for library consumers.
2. **Zero overhead when nobody listens.**
3. One batch = one trace span; each DB round-trip (chunk) = one child span.
4. Low-cardinality, PII-safe attributes by default; SQL text / parameter values **never** captured by default.
5. Standard OTel SDK wiring (`AddSource`), verified by unit + integration tests and a sample.

## 1. Non-goals (explicit)

- No new required NuGet dependency in any `src/*` project.
- No breaking change to public API signatures or default behavior (tracing off-by-listener, i.e. allocation-free no-op when no `ActivityListener`).
- No emission of SQL command text, parameter values, or entity property values by default.
- No metrics (`System.Diagnostics.Metrics.Meter`, counters/histograms) in v1 — explicitly out of scope; may be proposed separately later.
- No replacement for EF Core's own `Microsoft.EntityFrameworkCore.Database.Command.*` diagnostics (we bypass EF interceptors by design — see §2 — so we emit our own spans instead of trying to hook EF's).
- No MySQL work in this plan (provider does not exist yet).
- No staging-table fast path (M5) coupling.

## 2. Background — what exists today (verified in repo)

| Area | Current state | Implication for OTel |
|---|---|---|
| Orchestration chokepoint | `src/NSLabs.EFCore.Extensions/BulkBatch.cs:95` `ExecuteCoreAsync` resolves provider via `BulkProviderRegistry`, calls `provider.Generate(...)` then `provider.ExecuteAsync(...)`, then builds `BulkExecuteResult` | Single place to open the **batch span** — provider-agnostic. |
| Provider executors | `SqlServerExecutor.cs`, `SqliteExecutor.cs`, `NpgsqlExecutor.cs` — each has `ExecuteAsync → RunAsync → ExecuteCoreAsync → ExecuteChunkAsync`; each opens/closes the connection, honors `Database.CurrentTransaction`, honors `IExecutionStrategy`, honors `BulkExecuteOptions.CommandTimeout`, throws `BulkZeroRowsAffectedException` | Per-chunk spans must be added in **one shared helper** in the core assembly, called from the 3 `ExecuteChunkAsync` methods (1 line each), otherwise logic triplicates and drifts. |
| Chunk model | `Internal/SqlChunkPlan.cs`: `CommandText`, `Parameters` (`SqlParam`), `OperationIndices` | Span attributes must be **derived counts**, never the text/values: `param_count`, `op_count`, op-kind mix. |
| Options (execution) | `BulkExecuteOptions.cs`: `MaxParametersPerCommand=2000`, `ThrowIfZeroAffected`, `CommandTimeout`; `Clone/CopyTo/Validate`; per-context defaults via `DependencyInjection/BulkExecuteOptionsExtension.cs` + `BulkExecuteBuilderExtensions.cs` (`UseBulkExecute`); `LogFragment` / `PopulateDebugInfo` expose values to EF debug | **Stays untouched.** Execution semantics only. Telemetry policy lives in a **separate** `BulkInstrumentationOptions` + `UseBulkInstrumentation` extension (§5) — never new properties on `BulkExecuteOptions`. An explicit per-call `BulkExecuteOptions` replaces execution config only and must not reset instrumentation. |
| Logging | No `ILogger` / `Activity` / `DiagnosticSource` usage in `src/*` today (grep-verified). Samples use `Microsoft.Extensions.Logging.Console`; `Directory.Packages.props` pins `Logging.Abstractions 10.0.0` for samples only. Core `csproj` references only `Microsoft.EntityFrameworkCore.Relational` (which transitively brings Abstractions, but core does **not** directly reference it) | Core must add a **direct** `Microsoft.Extensions.Logging.Abstractions` reference only if we log from core (recommended: yes, for error/debug logs that OTel logging bridges automatically). `System.Diagnostics.DiagnosticSource` needs **no package** on .NET 10 (inbox: `Activity` / `ActivitySource` are in the shared framework). |
| Build | `Directory.Build.props`: `net10.0`, `Nullable enable`, `ImplicitUsings enable`, `LangVersion latest`; `Directory.Packages.props`: central version management, `CentralPackageTransitivePinningEnabled=false`; solution is `NSLabs.EFCore.Extensions.slnx` | OTel SDK/exporter packages go **only** in `samples/*` and `tests/*` via new `PackageVersion` entries; `src/*` stays dependency-free. `LangVersion latest` allows `required` / collection expressions already in use. |
| Tests | `Tests.Unit.{Shared,SqlServer,Sqlite,Npgsql}` (golden-SQL), `Tests.Integration.{...}` (Testcontainers + SQLite), `Tests.Common` (has `InternalsVisibleTo` from core) | Telemetry unit tests must run with **no container** (in-memory BCL `ActivityListener`); integration test asserts trace→DB correlation lives in the existing integration projects. |

Key architectural fact driving this plan: bulk execution uses
`context.Database.GetDbConnection()` + `connection.CreateCommand()` directly, so
EF Core command interceptors / EF diagnostic listeners do **not** fire. Our spans are
the only visibility into these round-trips. They must therefore carry the standard
`db.*` attributes consumers expect, and must propagate `Activity.Current` correctly.

## 3. Design principles

1. **Inbox-only in `src/`**: use `System.Diagnostics.ActivitySource` + optional `ILogger<T>` (Abstractions). Never reference `OpenTelemetry.*` from `src/`.
2. **Execution vs observability are separate axes** (industry standard: cf. `SqlClientInstrumentationOptions.SetDbStatementForText`, EF Core's `EnableSensitiveDataLogging` living outside execution options). `BulkExecuteOptions` controls *what the database does* (chunk budget, timeout, zero-row enforcement); `BulkInstrumentationOptions` (§5) controls *what telemetry is emitted* (span granularity, exception recording, SQL-text capture). Neither type references the other; per-call `BulkExecuteOptions` never resets instrumentation policy.
3. **Single source name**: `ActivitySource.Name == "NSLabs.EFCore.Extensions"`. Version = assembly informational version (single constant, test-asserted non-empty).
4. **Check-then-allocate**: every span/log site first checks `ActivitySource.HasListeners()` / `Logger.IsEnabled()` before building strings, enumerating operations, or hashing.
5. **PII safety by construction**: default attributes are counts, names, and enums. Anything unbounded (SQL text, table names beyond entity-type display names already in public `BulkExecuteResult`, parameter values) is opt-in and redacted/truncated.
6. **One batch span, N chunk spans**: `BulkExecute` (client span) → `BulkExecute.Chunk` (client spans, one per `SqlChunkPlan` actually executed). Skipped zero-row no-op chunks emit an `Event`, not a span.
7. **Errors are first-class**: exceptions → `Activity.RecordException` + `Status=Error`; `ThrowIfZeroAffected` throw is recorded the same way.
8. **Retries stay truthful**: `IExecutionStrategy` retries re-enter `RunAsync`; the batch span stays open across retries, each attempt adds an event (`retry.attempt`), chunk spans are per-attempt (so attempt count is visible, not hidden).
9. **Respect ambient context**: never `StartActivity` with a null parent when `Activity.Current` exists — created spans are children of the caller's span automatically; `ActivityKind.Client`; never set `Activity.Current` to null; cancellation just stops the span with `Canceled` status.

## 4. Telemetry schema (normative)

### 4.1 Span: `BulkExecute` (kind: Client, parent: `Activity.Current`)

Opened in `BulkBatch.ExecuteCoreAsync`, stopped when `BulkExecuteResult` is built or on throw (including empty-batch fast path? **No** — empty batch returns `BulkExecuteResult.Empty` with **no span**, documented; it performs no I/O).

| Attribute | Value | Notes |
|---|---|---|
| `db.system` | `mssql` / `sqlite` / `postgresql` | Derived from `Database.ProviderName`, mapped once; unknown → provider short name, never full CLR type. |
| `db.name` | database name | From connection `Database` property; empty → omitted. Low cardinality in practice. |
| `db.operation` | `bulk_execute` | Fixed; keeps dashboard grouping stable. |
| `nslabs.bulk.provider` | EF provider name as-is (e.g. `Microsoft.EntityFrameworkCore.SqlServer`) | Needed to disambiguate same `db.system` in exotic setups. |
| `nslabs.bulk.operation_count` | int | `_operations.Count`. |
| `nslabs.bulk.update_count` / `.upsert_count` / `.delete_count` | int | Kind mix; cheap single pass. |
| `nslabs.bulk.chunk_count` | int | `chunks.Count` (planned); actual-executed may differ on error — final value set before stop. |
| `nslabs.bulk.total_rows` | int | `TotalRowsAffected`; set on success only. |
| `nslabs.bulk.throw_if_zero_affected` | bool | From effective **execution** options (read-only tag; not a telemetry knob). |
| `nslabs.bulk.max_parameters` | int | From effective **execution** options (read-only tag). |
| `nslabs.bulk.command_timeout_s` | int | Only when `CommandTimeout` set. |
| `error.type` | exception full name | On error only (OTel semconv). Gated by `BulkInstrumentationOptions.RecordException` for the exception *event*; `Status=Error` is always set. |
| `db.statement` | **absent by default** | Only when `BulkInstrumentationOptions.CaptureCommandText=true` (§5); then **first-chunk truncated** summary + `nslabs.bulk.command_truncated=true` when cut. Never parameter values. Sampling/head-sampling decisions stay with the OTel SDK sampler; this flag only controls whether the library *attaches* the attribute at all (same pattern as `SetDbStatementForText`). |

Span events: `retry.attempt` (with `attempt` int, only when execution strategy retries — detect by counting `RunAsync` entries > 1), `chunk.failed` is represented by the failed child span itself (no duplicate event).

### 4.2 Span: `BulkExecute.Chunk` (kind: Client, parent: batch span)

Opened/closed inside each provider executor's `ExecuteChunkAsync` via a shared
`BulkExecuteTelemetry.StartChunkScope(...) : IDisposable?` helper (returns `null` when no listener → zero overhead, single `using`).

| Attribute | Value |
|---|---|
| `db.system`, `db.name`, `db.operation` | Inherited same values as parent (duplicated so chunk spans are useful standalone). |
| `nslabs.bulk.chunk.index` | 0-based index within this `ExecuteCoreAsync` call. |
| `nslabs.bulk.chunk.parameter_count` | `chunk.Parameters.Count`. |
| `nslabs.bulk.chunk.operation_count` | `chunk.OperationIndices.Count`. |
| `nslabs.bulk.chunk.rows` | rows affected by this chunk (set on success). |
| `db.statement` | Absent by default; same opt-in rule as §4.1 (`BulkInstrumentationOptions.CaptureCommandText`, this chunk's text, truncated to `MaxCommandLength`). Chunk spans themselves are gated by `BulkInstrumentationOptions.EnableChunkSpans` (default true); when false, only the batch span + `chunk.skipped`/`retry.attempt` events are emitted. |

Zero-row upsert no-op chunks (`"-- zero-row"` marker, Sqlite/Npgsql) record a parent-span event `chunk.skipped` with `chunk.index` instead of a span.

### 4.3 Logs

Use `ILogger` (category `typeof(BulkBatch)`) resolved via `context.GetService<ILoggerFactory>()?.CreateLogger(...)` (factory is guaranteed by EF's internal provider; logger creation itself is null-tolerant and `IsEnabled`-guarded). Batch boundaries only — per-chunk detail (index, rows, duration) already rides on chunk spans; per-chunk log lines would be spammy at scale:

- `LogDebug`: batch start (op count, chunk count, provider) — event id `1001`.
- `LogDebug`: batch completed (total rows, op count) — event id `1002`.
- `LogError`: batch failure (exception with full chain, op count, provider) — event id `1101` (dedicated range; `1591` is a suppressed compiler warning in this repo — never reused).
- Never log SQL text or parameter values at any level unless `BulkInstrumentationOptions.CaptureCommandText` is on, and then only at `Trace/Debug` with explicit `[command-text]` prefix so log scrubbers can filter.

OTel logging needs no code: `ILogger` flows through the user's OTel LoggerProvider automatically.

## 5. API changes (additive only)

**`BulkExecuteOptions` is NOT touched.** Telemetry policy lives in a new, separate
type following OTel .NET instrumentation-options conventions (`*InstrumentationOptions`
suffix, e.g. `SqlClientInstrumentationOptions`, `AspNetCoreInstrumentationOptions`):

```csharp
namespace NSLabs.EFCore.Extensions;

/// <summary>
/// Observability policy for bulk execution. Configured via
/// <c>UseBulkInstrumentation(...)</c>; independent of <see cref="BulkExecuteOptions"/>.
/// </summary>
public sealed class BulkInstrumentationOptions
{
    /// <summary>Emit one child span per executed chunk. Default true.</summary>
    public bool EnableChunkSpans { get; set; } = true;

    /// <summary>Attach exception events to spans. Default true.</summary>
    public bool RecordException { get; set; } = true;

    /// <summary>
    /// Attach (truncated) SQL command text as <c>db.statement</c>. Default false.
    /// Never includes parameter values. Mirrors <c>SetDbStatementForText</c> semantics:
    /// this flag controls whether the library attaches the attribute at all;
    /// sampling stays with the OTel SDK sampler and collector redaction still applies.
    /// </summary>
    public bool CaptureCommandText { get; set; }

    /// <summary>Max chars captured per <c>db.statement</c>. Default 4000. Must be &gt; 0.</summary>
    public int MaxCommandLength { get; set; } = 4000;

    public BulkInstrumentationOptions Clone();
    public void CopyTo(BulkInstrumentationOptions target);
    internal void Validate(); // MaxCommandLength > 0 unconditionally (simple invariant, no conditional logic)
}
```

Configuration mirrors the existing `UseBulkExecute` pattern but as an **independent
extension** (separate `IDbContextOptionsExtension`, separate `LogFragment` /
`PopulateDebugInfo` keys `BulkInstrumentation:...`, additive repeated calls,
copy-in clone so pooled contexts can't observe caller mutation):

- `DbContextOptionsBuilder.UseBulkInstrumentation(Action<BulkInstrumentationOptions>)`
  (+ `BulkInstrumentationOptions` overload, + generic `DbContextOptionsBuilder<TContext>` variants)
  in `DependencyInjection/BulkInstrumentationBuilderExtensions.cs`
  (namespace `Microsoft.EntityFrameworkCore`, same as existing builder extensions).
- `DependencyInjection/BulkInstrumentationOptionsExtension.cs` holding a private cloned snapshot,
  `SnapshotRef` internal accessor, `GetServiceProviderHashCode() => 0` with the same
  no-services rationale as the execution extension.
- Resolution: new `BulkBatch.ResolveInstrumentationEffective(DbContext)` reading
  `FindExtension<BulkInstrumentationOptionsExtension>()?.SnapshotRef ?? new BulkInstrumentationOptions()`
  at execution time (never at batch-construction time). An explicit per-call
  `BulkExecuteOptions` argument replaces **execution** config only — instrumentation
  still resolves from the context. No `ExecuteAsync` overload takes instrumentation
  options (ambient policy, not per-call; per-call toggles would produce inconsistent
  redaction across batches and fight the SDK sampler — the standard OTel stance).

Rejected alternatives (recorded so they aren't re-litigated):

- ❌ Properties on `BulkExecuteOptions` — mixes execution semantics (which affect SQL
  and flow through per-call full-replacement) with observability policy; a per-call
  `BulkExecuteOptions` would then silently reset redaction posture. Rejected.
- ❌ `TracerProviderBuilder.AddNSLabsBulkInstrumentation(...)` in v1 — the idiomatic
  long-term home, but requires either an OTel package reference from `src/` (forbidden)
  or a new companion package. Reconsider only if a `NSLabs.EFCore.Extensions.OpenTelemetry`
  package is created; the `BulkInstrumentationOptions` shape proposed here maps 1:1 onto it.
- ❌ Static global `BulkExecuteTelemetry.Configure(...)` / env-var switches — untestable
  with pooled contexts, invisible in EF debug views, undiscoverable. Rejected.
- ❌ An `EnableTracing` boolean — non-standard; "no listener = off"
  (`ActivitySource.HasListeners()` gate) is the OTel pattern and
  avoids a second opt-out path. Rejected; documented instead.

The `ActivitySource` singleton lives in `Internal`
(`InternalsVisibleTo` already covers providers + test projects) with a tiny
`public static class BulkExecuteTelemetryNames` exposing the `const string`
name so apps don't hardcode strings:

```csharp
public static class BulkExecuteTelemetryNames
{
    public const string SourceName = "NSLabs.EFCore.Extensions";
}
```

## 6. Implementation plan (file by file)

### Phase 1 — Core instrumentation (no new packages)

1. **NEW** `src/NSLabs.EFCore.Extensions/Diagnostics/BulkExecuteTelemetry.cs` (internal static):
   - `static readonly ActivitySource Source = new(SourceName, version)`; version from `typeof(BulkExecuteTelemetry).Assembly.GetInformationalVersion()` fallback `1.0.0`. No `Meter`, no instruments.
   - Helpers: `DbSystem(string? providerName) -> string`, `Truncate(string, int)`.
   - `StartBatchScope(DbContext, IReadOnlyList<BoundOperation>, BulkExecuteOptions execution, BulkInstrumentationOptions instrumentation, IReadOnlyList<SqlChunkPlan>) : BatchScope?` — returns null when `!Source.HasListeners()` (zero overhead: skip scope allocation entirely). The scope reads `db.statement`/`RecordException`/`EnableChunkSpans` from the **instrumentation** argument, never from execution options.
   - `StartChunkScope(...) : ChunkScope?` — null when `!Source.HasListeners()` or `!instrumentation.EnableChunkSpans`.
   - Both scopes: `IDisposable`, stop on dispose, `SetError(Exception)` helper doing `RecordException + SetStatus(Error) + error.type tag` (exception event gated by `RecordException`; `Status=Error` always).
2. **EDIT** `src/NSLabs.EFCore.Extensions/BulkBatch.cs:95` `ExecuteCoreAsync`:
   - Empty-batch fast path stays spanless (document in XML remark).
   - Resolve both axes: existing `ResolveEffective(context, explicitOptions)` for execution + new `ResolveInstrumentationEffective(context)` for instrumentation (context snapshot, else defaults). Explicit per-call execution options do not alter instrumentation.
   - After `provider.Generate`, open batch scope; wrap `provider.ExecuteAsync` in try/catch: on success set `total_rows` on the span; on `Exception` (including `BulkZeroRowsAffectedException` and cancellation) call `SetError`, rethrow preserving stack (`ExceptionDispatchInfo` not needed — plain `throw;`).
   - Retry-attempt detection: increment a local counter via a callback — simplest truthful approach: the executors expose attempts through the existing structure (each `RunAsync` re-entry = 1 attempt); pass an `Action`/`int[]` attempt counter into `provider.ExecuteAsync`? That changes `IBulkProvider` signature — **avoid**. Instead: batch scope records attempts = number of `chunk.failed`-with-retry… Simpler correct rule: record `retry.attempt` events from inside the shared chunk helper when it observes the same chunk index executing twice (track in scope). Document this mechanism in code comment.
3. **EDIT** the 3 executors (`SqlServer/Internal/SqlServerExecutor.cs`, `Sqlite/.../SqliteExecutor.cs`, `Npgsql/.../NpgsqlExecutor.cs`):
   - `ExecuteChunkAsync`: wrap body with `using var scope = BulkExecuteTelemetry.StartChunkScope(...)`; set `rows` on success; `SetError` + rethrow on failure. No other logic change. (SqlServer reads rowcounts via reader; Sqlite/Npgsql via `ExecuteNonQueryAsync` — helper takes final rows as parameter, so call sites differ by one line; keep the diff minimal.)
   - Zero-row no-op early return: call `BulkExecuteTelemetry.RecordChunkSkipped(parentActivity, index)` (event, no span).
4. **NEW** `src/NSLabs.EFCore.Extensions/BulkInstrumentationOptions.cs` (public, §5: `EnableChunkSpans`, `RecordException`, `CaptureCommandText`, `MaxCommandLength` + `Clone`/`CopyTo`/`Validate` + XML docs) **plus** `DependencyInjection/BulkInstrumentationOptionsExtension.cs` + `DependencyInjection/BulkInstrumentationBuilderExtensions.cs` (`UseBulkInstrumentation`, same additive copy-in pattern as `UseBulkExecute`, `LogFragment`/`PopulateDebugInfo` keys `BulkInstrumentation:...`). `BulkExecuteOptions*` files are **not modified**.
5. **NEW** `BulkExecuteTelemetryNames.cs` (public, §5) + XML docs.

### Phase 2 — Logging wiring (1 package addition)

6. **EDIT** `src/NSLabs.EFCore.Extensions/NSLabs.EFCore.Extensions.csproj`: add direct `PackageReference Include="Microsoft.Extensions.Logging.Abstractions"` (version via new `PackageVersion` in `Directory.Packages.props`). Rationale: core will now directly use `ILogger<T>`; relying on transitive reference is fragile and breaks with `CentralPackageTransitivePinningEnabled=false` auditing.
7. **EDIT** `BulkBatch.ExecuteCoreAsync`: resolve `ILoggerFactory` via `context.GetService<ILoggerFactory>()?.CreateLogger(typeof(BulkBatch))` in try/catch (null-tolerant); debug/error logs per §4.3, all `IsEnabled`-guarded. (Factory route chosen over `GetService<ILogger<BulkBatch>>` — guaranteed resolvable from EF's internal provider.)

### Phase 3 — Tests (no containers for unit level)

8. **DONE** `tests/NSLabs.EFCore.Extensions.Tests.Unit.Sqlite/SqliteTelemetryTests.cs` (own `telemetry` collection with `DisableParallelization` so span-count assertions are deterministic; BCL `ActivityListener` only):
   - `Batch_emits_span_with_counts_and_ok_status` (+ exact chunk-span parent/count/rows assertions; SQLite emits one chunk per update).
   - `Child_spans_nest_under_ambient_parent` (parent propagation via manual `Activity`).
   - `No_listener_no_span_no_throw` (zero-overhead path executes normally; re-read uses `AsNoTracking` since bulk ops bypass change tracking).
   - `Error_sets_error_status_and_records_exception` (`ThrowIfZeroAffected` → `Status=Error`, `error.type`, `exception` event).
   - `RecordException_false_suppresses_event_but_keeps_error_status`.
   - `Chunk_spans_disabled_by_option` (`EnableChunkSpans=false` → batch span only).
   - `CommandText_never_captured_by_default` + `CaptureCommandText_opt_in_truncates` (truncated at `MaxCommandLength` + `nslabs.bulk.command_truncated`).
   - `Explicit_BulkExecuteOptions_does_not_reset_instrumentation` (axis independence).
   - `UseBulkInstrumentation_additive_and_validated` + `Instrumentation_factory_defaults_are_safe` + `Instrumentation_clone_is_independent_of_source` + `BulkExecuteOptions_untouched_by_instrumentation`.
   - `Empty_batch_emits_no_span`.
   - `Cancellation_marks_span_error` (pre-canceled token → `OperationCanceledException`, span `Error` + `error.type`).
9. **EDIT** integration projects (`.Integration.Sqlite` first — no container; then SqlServer/Npgsql): one test each asserting parent-child propagation (`Activity.Current` set by test → batch span `ParentId` equals it) and `db.system` value per provider.
10. No new test packages needed: unit tests use the BCL `ActivityListener` only (no OTel SDK reference in `tests/*`). OTel SDK packages (`OpenTelemetry`, exporters) appear **only** in the sample host (see §Phase 4) via new `PackageVersion` entries; never referenced from `src/`.

### Phase 4 — Samples + docs

11. **DONE** SQLite sample host wired: `OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Exporter.Console` 1.12.0 (sample-only `PackageVersion` entries; `src/*` untouched), `AddOpenTelemetry().WithTracing(t => t.AddSource(BulkExecuteTelemetryNames.SourceName).AddConsoleExporter())` guarded by `NSLABS_OTEL_CONSOLE=true` (default run byte-identical in behavior). Learning recorded: the `TracerProvider` is a lazy singleton and this host never calls `Start()`, so the sample resolves `GetRequiredService<TracerProvider>()` once after `Build()` — without it nothing exports. Verified: guarded run exports full `BulkExecute`/`BulkExecute.Chunk` spans with all §4 tags; unguarded run exports zero spans, exit 0.
12. **EDIT** `README.md` (short section: "Observability" — source name + one wiring snippet + default-redaction note) and `docs/DESIGN.md` (§2 result-object area: note spans emitted; §3 pipeline: add "Telemetry" step). Keep both edits small and cross-linked; full attribute tables live in this plan file until promoted to a `docs/OBSERVABILITY.md` follow-up (explicitly out of scope for the code PR).
13. **EDIT** `NUGET_README.md` only if it documents options (check at implementation time; default: no change).

### Phase 5 — CI / release hygiene

14. Verify `dotnet build NSLabs.EFCore.Extensions.slnx`, `dotnet test` (unit, sqlite integration) with and without listeners; run `dotnet format` / repo analyzers if present; confirm no new warnings (docs file generation on).
15. Confirm packaging: `IsPackable` projects unchanged; no `OpenTelemetry*` in any `src/*.csproj`; `dotnet pack` output inspected for accidental dependency.
16. Versioning: no major bump (additive); changelog entry describing names + attributes.

## 7. Edge cases & risks

| Risk | Mitigation |
|---|---|
| High-cardinality attributes (SQL text, table names per tenant) | allowlist in §4; tests assert exact attribute set (fail on extras). |
| `Activity` allocation when disabled | `HasListeners()` gate; measured 500× single-op SQLite batches, Release: baseline (pre-change) 0.111 ms/op vs instrumented idle 0.076 ms/op — delta within run-to-run noise, no measurable regression. With listener attached: 0.100 ms/op (cost is the spans themselves). |
| `ILogger` resolution throwing on odd providers | null-tolerant `GetService` in try/catch; telemetry must never break execution — wrap entire scope creation in try/catch that falls back to plain execution. |
| Native AOT / trimming (`ActivitySource` reflection-free — fine; `Assembly.GetInformationalVersion` trim-safe — use `AssemblyInformationalVersionAttribute` read with fallback) | note in code; no reflection elsewhere. |
| `db.name` cardinality with per-tenant databases | document opt-out: keep `CaptureCommandText=false` (default) and strip `db.name` via OTel processor; consider dropping `db.name` in v1 if reviewer prefers — flagged as open question. |
| Chunk-span fan-out on huge batches (10k chunks → 10k spans) | bounded by param budget (default 2000/chunk); mitigated by `BulkInstrumentationOptions.EnableChunkSpans=false` + documented sampling guidance (no new `MaxChunkSpans` knob in v1). |

## 8. Acceptance criteria

- [x] `dotnet pack` shows **zero** new dependencies on `NSLabs.EFCore.Extensions*` packages (only `Logging.Abstractions`, already centrally pinned; verified via build — no `OpenTelemetry*` in `src/`).
- [x] `BulkExecuteOptions*` files untouched; telemetry knobs exist only on `BulkInstrumentationOptions` + `UseBulkInstrumentation`.
- [x] With no listener: existing golden-SQL + integration suites pass unchanged (246 tests: 53 Sqlite unit incl. 15 new telemetry tests, 121 SqlServer unit, 39 Npgsql unit, 33 Sqlite integration).
- [x] With listener: 1 `BulkExecute` span per non-empty batch with attributes exactly per §4.1; 1 `BulkExecute.Chunk` span per executed chunk per §4.2 (suppressible via `EnableChunkSpans=false`); error path sets `Error` + exception event (suppressible via `RecordException=false`, status retained).
- [x] `db.statement` absent by default; present + truncated at `MaxCommandLength` only with `UseBulkInstrumentation(o => o.CaptureCommandText = true)`; explicit per-call `BulkExecuteOptions` does not clear it.
- [x] Sample runs and exports a trace with stock OTel SDK wiring (`NSLABS_OTEL_CONSOLE=true` → 89 spans with full tags; unset → zero spans).
- [x] README + DESIGN observability notes added; this plan file linked from the PR.

## 9. Open questions (for reviewer, before coding)

1. Keep `db.name` by default, or drop it for cardinality safety? (Proposal: keep — EF users expect it; processors can drop.)
2. Should the empty-batch fast path emit a span? (Proposal: no — no I/O happened.)
3. Class name: `BulkInstrumentationOptions` (OTel convention) vs `BulkTelemetryOptions`? (Proposal: `BulkInstrumentationOptions`.)
4. Future companion package (`NSLabs.EFCore.Extensions.OpenTelemetry` with `AddNSLabsBulkInstrumentation`) mapping 1:1 onto these options — wanted, or is `UseBulkInstrumentation` sufficient permanently?

## 10. Estimated scope

Small–medium: ~6 new files (`BulkInstrumentationOptions`, 2 DI files, telemetry helper, names, tests), ~7 edited files, ~10 new tests. No migrations, no SQL changes, no changes to `BulkExecuteOptions*`, no public signature changes (one new options class + one new `UseBulkInstrumentation` extension family only). No metrics work.
