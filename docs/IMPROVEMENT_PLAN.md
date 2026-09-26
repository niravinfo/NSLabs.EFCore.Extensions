# Improvement Plan

Prioritized plan for performance, correctness, usability, and maintainability improvements.
Excludes anything proposed in `NEXT_FEATURES_PROPOSAL.md`.

Status legend: `TODO` | `IN PROGRESS` | `DONE` | `BLOCKED` | `SKIPPED`

---

## Priority overview

| # | Item | Category | Priority | Effort | Status |
|---|------|----------|----------|--------|--------|
| P1 | Benchmark suite (BenchmarkDotNet) | Performance | Critical | Medium | DONE |
| P2 | Cache expression compilation in translators | Performance | Critical | Medium | DONE |
| P3 | Fast path for entity-style match updates | Performance | High | Medium | TODO |
| P4 | StringBuilder-direct SQL emission | Performance | High | Large | DONE |
| P5 | Fix provider registry thread-safety race | Correctness | Critical | Small | DONE |
| P6 | Fix null-validation gaps in DbSet extensions | Correctness | High | Small | DONE |
| P7 | Validate options before user callback runs | Correctness | Medium | Small | DONE |
| P8 | XML docs on public API surface | Usability | High | Large | TODO |
| P9 | Add `DbSet.BulkDeleteAsync` sugar + expose entity-row delete | Usability | Medium | Medium | TODO |
| P10 | Unify builder verb naming (`Set`/`Update`/`SetProperty`) | Usability | Medium | Small | TODO |
| P11 | Fix `AddNSLabsBulkInstrumentation` DI semantics | Usability | Medium | Medium | TODO |
| P12 | Emit `BulkExecuteRetrying` (60005) log event | Usability | Low | Small | TODO |
| P13 | API baseline tooling (PublicAPI/ApiCompat) | Maintainability | High | Medium | TODO |
| P14 | Test gaps: retry, registry, re-execution, concurrency, DI | Testing | High | Large | IN PROGRESS |
| P15 | Unify logging/PII verbosity gates | Usability | Low | Small | TODO |
| P16 | Micro-alloc fixes (lists, reflection, param names, etc.) | Performance | Medium | Medium | TODO |

---

## P1 — Benchmark suite (BenchmarkDotNet)

**Why first:** Nothing in the repo measures performance. Every optimization below needs a baseline and regression protection.

**Database requirement (decision):** No real database needed for the paths this plan optimizes:

| Scenario | Backend | Rationale |
|----------|---------|-----------|
| Bind / translation (P2) | None (offline) | Expression→`SqlNode` is pure CPU; uses the same fake-connection harness pattern as golden-SQL tests |
| SQL generation (P4) | None (offline) | `provider.Generate(...)` → chunk plans, no I/O |
| Entity-row match (P3) | None (offline) | Bind + generate only |
| Large IN lists | None (offline) | Emit-path only |
| End-to-end execute | In-memory SQLite only | Isolates command-prep/param/reader cost; no network noise |
| Real SqlServer/Npgsql | Optional, manual | Absolute numbers only — too noisy + Docker-dependent for regression gating |

**Work:**
- Add `benchmarks/NSLabs.EFCore.Extensions.Benchmarks` project (BenchmarkDotNet 0.15.8 via Central Package Management).
- Self-contained benchmark model (no coupling to `Tests.Common`).
- `InternalsVisibleTo` granted from core + SqlServer + Sqlite (generation harness needs `SqlServerProvider`, `BulkBatch.Operations`, `SqlChunkPlan`).
- Scenarios and sizes:
  - **Predicate bind:** 10 / 1,000 / 10,000 ops — comparison + `StartsWith` + captured-closure compile fallback (P2 target).
  - **Computed SET bind:** 10 / 1,000 / 10,000 ops — `Set(x => x.Key2, x => x.Key2 + (factor * 2))` hits uncached `SetExpressionTranslator.Evaluate` (P2 target).
  - **SQL generation (SqlServer):** same sizes via offline harness (P4 target).
  - **Entity-row match:** 10 / 1,000 / 10,000 rows with `(row, x) => x.Id == row.Id` — per-row rewrite path (P3 target); PK-match variant as contrast.
  - **Large IN lists:** 500 / 5,000 / 20,000-element `Contains` (above SqlServer threshold 100 / Sqlite threshold 50 → fast JSON path).
  - **Execute (SQLite in-memory):** update / entity-row / large-IN end-to-end.
- `[MemoryDiagnoser]` on a shared base; default job; BenchmarkDotNet CLI args pass through for filtering.
- How to run documented in `benchmarks/README.md`.

**Project structure:**
```
benchmarks/
└── NSLabs.EFCore.Extensions.Benchmarks/
    ├── NSLabs.EFCore.Extensions.Benchmarks.csproj   # OutputType=Exe, not packable
    ├── Program.cs                                    # BenchmarkSwitcher
    ├── BenchmarkBase.cs                               # [MemoryDiagnoser] base
    ├── Infrastructure/
    │   ├── BenchmarkModel.cs                          # self-contained entities + context
    │   ├── SqlServerGenerationHarness.cs              # offline Generate (fake connection string)
    │   └── SqliteDatabaseFactory.cs                   # shared-cache in-memory SQLite + seed
    ├── Bind/
    │   ├── PredicateTranslationBenchmarks.cs           # P2
    │   └── SetExpressionBenchmarks.cs                 # P2
    ├── Generation/
    │   ├── SqlGenerationBenchmarks.cs                 # P4
    │   ├── EntityRowMatchBenchmarks.cs                # P3
    │   └── LargeInListBenchmarks.cs
    └── Execution/
        └── SqliteExecuteBenchmarks.cs                 # in-memory end-to-end
```

**Files:**
- New: project tree above + `benchmarks/README.md`.
- New: `.github/workflows/benchmarks.yml` — `workflow_dispatch` only (inputs: `--filter`, `--job` Dry/Short/Default; ubuntu-latest; Release; uploads `BenchmarkDotNet.Artifacts/` for 90 days; `concurrency: benchmarks` queued, never parallel). Not wired into push/PR CI.
- Edit: `NSLabs.EFCore.Extensions.slnx` (add `/benchmarks/` folder), `Directory.Packages.props` (add `BenchmarkDotNet`), core/SqlServer/Sqlite `.csproj` (`InternalsVisibleTo`).

**CI notes:**
- Solution `dotnet build` compiles benchmarks (regression compile gate).
- `dotnet test` does not run them (no test-framework reference; not an MTP test project).
- `dotnet pack` skips them (`IsPackable=false` default).
- **Run on GitHub (preferred — no local run needed):** Actions → *Benchmarks* → *Run workflow*, set `filter`/`job`, download the `benchmark-results-*` artifact. Compare only runs from this workflow (same runner image); never mix with local Windows numbers. `Allocated` is the more trustworthy CI signal than `Mean` on shared runners.
- Local alternative: `dotnet run -c Release --project benchmarks/NSLabs.EFCore.Extensions.Benchmarks -- --filter '*Predicate*'`.
- `BenchmarkDotNet.Artifacts/` already git-ignored.

**Acceptance:**
- Project builds in Release as part of the solution.
- Each benchmark class runs and produces time + allocation columns (verified via Dry-job smoke: all 6 classes / 12 benchmarks executed).
- Manual GitHub Actions workflow runs the suite on demand and uploads results.
- Pre-optimization baseline captured (via the workflow) before P2/P3/P4 land.

**Depends on:** none. Status: DONE — suite built + smoke-verified + workflow added; Short-job baseline captured 2026-09-23 in `benchmarks/BASELINE.md` before P2.

---

## P2 — Cache expression compilation in translators

**Why:** Both translators call `Expression.Compile()` on every bind with no cache — the hottest bind-time CPU cost. EF Core caches this class of compilation; the code comments acknowledge it and deliberately don't.

**Work:**

1. `src/NSLabs.EFCore.Extensions/Internal/SetExpressionTranslator.cs:381-388` — `Evaluate`:
   - Replace per-call `Expression.Lambda(...).Compile().Invoke()` with a cache keyed by expression identity/shape.
   - Use a static `ConcurrentDictionary` (or `ExpressionCache` helper) storing compiled `Func<object?>` delegates.
   - Keep the existing fast paths (member chains, constants) untouched.

2. `src/NSLabs.EFCore.Extensions/Internal/LinqPredicateTranslator.cs:479-488` — `Evaluate` fallback:
   - Remove `Compile().DynamicInvoke()` (reflection-based, slow) → typed compiled delegate.
   - Remove the `catch` → second-compile pattern; make the primary path correct for both value-type and reference-type expressions (single `Expression.Convert(object)` wrapper).
   - Cache compiled delegates the same way as (1).

3. Cache closure member access at `LinqPredicateTranslator.cs:419-435`:
   - Today: uncached `FieldInfo.GetValue` / `PropertyInfo.GetValue` per evaluation.
   - Change: cache a compiled getter per `MemberInfo` (mirrors the existing good pattern in `ModelBinder._getterCache`, `ModelBinder.cs:120-138`).

4. Cache the default-comparer check at `LinqPredicateTranslator.cs:326-330`:
   - Today: `MakeGenericType` + `GetProperty("Default").GetValue(null)` per 3-arg `Contains` call — allocates a closed `Type` each time.
   - Change: static generic cache (`static class DefaultComparerCache<T>` or `ConcurrentDictionary<Type, object>`).

**Files:**
- `src/NSLabs.EFCore.Extensions/Internal/SetExpressionTranslator.cs`
- `src/NSLabs.EFCore.Extensions/Internal/LinqPredicateTranslator.cs`
- Possibly new: `src/NSLabs.EFCore.Extensions/Internal/ExpressionEvaluatorCache.cs`

**Acceptance:**
- All existing golden-SQL + integration tests pass unchanged (SQL output must be identical).
- Benchmarks (P1) show reduced bind time and allocations for repeated predicate/SET shapes.
- Cache is bounded or keyed on stable expression identity (no unbounded growth per `DbContext` model rebuild — note the existing caveat on `ModelBinder` static caches).

**Depends on:** P1 (to measure). Status: DONE — `ExpressionEvaluatorCache` (getter + operator + comparer caches; `ConditionalWeakTable` compile fallback); tests green; A/B on same hardware (EPYC 7763, `benchmarks/BASELINE_New.md` vs `benchmarks/After_P2.md`): captured-subexpression SET bind **37–50× faster / −57% alloc**, closure compile fallback **54–76× faster / −64% alloc**, no regressions anywhere else.

---

## P3 — Fast path for entity-style match updates

**Why:** `BulkBatch.cs:604-606` rewrites the match expression **per row**: `ExpressionVisitor` traversal + boxed `Expression.Constant(row)` + new lambda + full `Translate` pass. O(rows × predicate-size) allocations. The no-match path (`:592-600`) already builds `SqlBinaryNode`s directly and is cheap — the match path has no equivalent.

**Work:**
- Detect the common equality-match shape: `match` body is a conjunction of `x.Prop == row.Prop` (or `row.Prop == x.Prop`) member comparisons between the two parameters.
- For that shape, build `SqlBinaryNode(Eq, column, parameter/value)` directly per row — no `ParameterReplacer`, no lambda allocation, no re-translate of the whole predicate.
- Fall back to the existing per-row rewrite path for arbitrary predicates (correctness preserved).
- Cover entity-row `Update(rows)` and the (currently unreachable) delete variant.

**Files:**
- `src/NSLabs.EFCore.Extensions/BulkBatch.cs` (~lines 560-610)
- `src/NSLabs.EFCore.Extensions/Internal/ParameterReplacer.cs` (unchanged; still used by fallback)
- Tests: `tests/NSLabs.EFCore.Extensions.Tests.Unit.SqlServer/MixedAndEntityRowTests.cs`, `EntityStyleExecutionTests` (both unit + integration, all three providers)

**Acceptance:**
- Identical generated SQL for entity-style match updates (golden tests unchanged or extended).
- Benchmark for 10k-row match update shows large allocation/time reduction vs baseline.
- Arbitrary (non-equality) match expressions still work via fallback.

**Depends on:** P1 (to measure); shares translation internals with P2 but can land independently.

---

## P4 — StringBuilder-direct SQL emission

**Why:** Every AST node in `Emit` returns a new interpolated string, which the caller then appends into the outer `StringBuilder` — N intermediate strings per chunk, each copied again. Largest per-chunk allocation source after bind-time compilation.

**Work:**
- Refactor `Emit(SqlNode, ...)` in all three generators from `string Emit(...)` → `void Emit(SqlNode, ..., StringBuilder sb)` (or a small `ref struct` writer), appending directly:
  - `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs:520-541`
  - `src/NSLabs.EFCore.Extensions.Npgsql/Internal/NpgsqlSqlGenerator.cs:359-384`
  - `src/NSLabs.EFCore.Extensions.Sqlite/Internal/SqliteSqlGenerator.cs:379-397`
- Replace the `EmitPredicate` multi-part `new StringBuilder(64)` (`SqlServerSqlGenerator.cs:393`) with the shared/pooled builder.
- Parameter names: `$"@p{Counter++}"` (`SqlServerSqlGenerator.cs:546`, Npgsql `:388`, Sqlite `:401`) — append into the shared builder or pre-cache `@p0..@pN` name strings (N bounded by max params per command, ~2100/65535).
- Keep `StringBuilderCache` (`Internal/StringBuilderCache.cs`) as the pooling mechanism; ensure `BuildChunk` and LIKE escaping still use it.

**Files:**
- All three `*SqlGenerator.cs` provider files.
- Golden-SQL tests across all three providers must pass byte-for-byte (normalized).

**Acceptance:**
- Zero change to generated SQL text.
- Benchmarks show reduced allocations per chunk generation.

**Depends on:** P1 (to measure). Larger refactor — do after P2/P3 for early wins.

**Status:** DONE — all three generators changed from `string Emit(...)` to `void Emit(StringBuilder, ...)` appending into the pooled `StringBuilder` (no intermediate interpolated strings; `EmitPredicate` uses the chunk builder; CONCAT/IN paths no longer allocate nested builders; pooled builder released on success *and* exception paths). SQL text, `@p{n}` numbering, and all exception types/messages preserved — fixed-arity methods (`UPPER`/`SUBSTRING`/`REPLACE`/…) use positional arg emission to match legacy indexing exactly. Verified by a temporary snapshot harness: **535/535 generated chunk plans byte-for-byte identical** to the pre-change baseline (SQL text + parameter names/values/types), plus all 457 tests green across 6 projects; independent code review found no reachable divergence. Harness removed before landing.

**CI A/B result (2026-09-25, `benchmarks.yml` `job=Short`, same runner — `benchmarks/BASELINE_New.md` → `After_P2.md` → `After_P4.md`):** `Allocated` dropped on **every** generator-reachable cell — entity-row match **−2.3%** (PK) / **−6.2%** (custom), update statements **−3.6% (−2,028 KB @ 10k)**, SQLite end-to-end execution up to **−1.4%** — with **no statistically significant `Mean` change anywhere** (every |Δ| is inside the reported 95% CI; per `benchmarks/README.md`, trust `Allocated` over `Mean` on shared runners). Large-IN is flat (−0.2 KB) as expected — all sizes exercise the OPENJSON single-parameter fast path. Bind-only benchmarks are byte-identical (the generators are unreachable from them); the one cell reading higher (`BindStartsWithPredicates` @ 10k, +1,406 KB) is byte-identical at 10/1k ops and on a path P4 never touches — a one-off ShortRun artifact, not a regression. The same two files also re-derive P2's documented wins exactly (closure fallback 54–76×, captured subexpression 37–50×).

---

## P5 — Fix provider registry thread-safety race

**Why:** `Internal/BulkProviderRegistry.cs` mutates a plain `Dictionary<string, IBulkProvider>` from both `Register` (`:16-20`) and the reflection fallback in `Resolve` (`:36`) with **no lock**. Concurrent first-use (two threads racing before `[ModuleInitializer]` runs, or overlapping `ExecuteAsync`) can corrupt the dictionary. This is a correctness bug on the public execution path.

**Work:**
- Replace `Dictionary` with `ConcurrentDictionary` for `_providers` (`:5`).
- Keep `KnownProviders` read-only table as-is.
- Verify `Register` and `Resolve`/`TryLoad` remain correct under concurrent writes (use `GetOrAdd` semantics for the fallback load so only one thread runs `Activator.CreateInstance`).

**Files:**
- `src/NSLabs.EFCore.Extensions/Internal/BulkProviderRegistry.cs`

**Acceptance:**
- Existing tests pass; new concurrency test (P14) hammering `Resolve` from parallel tasks does not throw/corrupt.

**Depends on:** none (can land immediately, independent of benchmarks)

**Status:** DONE — `_providers` is now a `ConcurrentDictionary<string, Lazy<IBulkProvider?>>`. `Lazy` (not bare `GetOrAdd`) is required: `GetOrAdd(key, factory)` does **not** serialize the factory, so it would still let several threads run `Activator.CreateInstance` concurrently. Failed loads are evicted with an atomic compare-and-remove (`TryRemove(KeyValuePair)`) so (a) a failed load is not cached as a permanent negative, and (b) a `Register` landing concurrently is not discarded. `KnownProviders` stays a plain `Dictionary` (read-only after static init). Race **reproduced before the fix** (1 failure in 30 stress runs against the old `Dictionary`) and gone after (0/30). Covered by `BulkProviderRegistryTests` in the SqlServer unit project.

---

## P6 — Fix null-validation gaps in DbSet extensions

**Why:** DbSet overloads throw `NullReferenceException` instead of `ArgumentNullException` for null `set`/`configure` — inconsistent with the correctly guarded `DbContext` overloads directly above them.

**Work:**
In `src/NSLabs.EFCore.Extensions/BulkBatchExtensions.cs`:
- `BulkUpdateAsync` (`:48-56`) and options overloads (`:63-72`): add `ArgumentNullException.ThrowIfNull(set)` and `ThrowIfNull(configure, nameof(configure))` before use.
- `BulkUpsertAsync` (`:74-82`, `:89-98`): same.
- Fix `GetContext(set)` cast chain (`:100-101`) so a null `set` surfaces as `ArgumentNullException(nameof(set))`, not NRE.

**Files:**
- `src/NSLabs.EFCore.Extensions/BulkBatchExtensions.cs`
- Tests: new facts asserting `ArgumentNullException` with correct `paramName` (all three provider unit projects or shared).

**Acceptance:**
- `db.Items.BulkUpdateAsync(null)` → `ArgumentNullException` with `paramName: "configure"`.
- Null `set` → `ArgumentNullException` with `paramName: "set"`.
- Matches the validation style of `DbContext.BulkExecuteAsync`.

**Depends on:** none

**Status:** DONE — `ArgumentNullException.ThrowIfNull(set)` + `ThrowIfNull(configure)` added as the first lines of all four DbSet overloads, matching the `DbContext` overloads' style. The plan's `GetContext` bullet needed **no edit**: guarding `set` at the call sites means the cast chain is unreachable with null, so it can no longer NRE. `set` is reported before `configure` (pinned by tests). Behavior change: these paths now throw `ArgumentNullException` instead of `NullReferenceException`. Covered by `ArgumentValidationTests`.

---

## P7 — Validate options before user callback runs

**Why:** `BulkBatchExtensions.cs:43-45` invokes `build(batch)` first; null/invalid `options` is only rejected afterwards inside `BulkBatch.ExecuteAsync` (`BulkBatch.cs:64-72`). User code runs before argument validation fails — violates .NET validation ordering conventions.

**Work:**
- In `DbContext.BulkExecuteAsync(build, options, ct)` overload: validate `options` (null check; if `Validate()` is reachable, run it) **before** `build(batch)`.
- Mirror for the `BulkUpdateAsync`/`BulkUpsertAsync` options overloads if they share the pattern.
- Add regression test: `BulkExecuteAsync(build: b => throw …, options: null)` must throw `ArgumentNullException` *without* invoking the callback.

**Files:**
- `src/NSLabs.EFCore.Extensions/BulkBatchExtensions.cs`
- `src/NSLabs.EFCore.Extensions/BulkBatch.cs` (only if validation helpers move)
- Tests: options-resolution / validation test class.

**Acceptance:**
- Callback never executes when `options` is null/invalid.
- Test proves ordering.

**Depends on:** none

**Status:** DONE — extracted `internal static BulkBatch.ValidateOptions(BulkExecuteOptions)` (null-check + `Validate()`) as the single definition of "usable options"; called by `BulkBatch.ExecuteAsync(options, ct)` and by all three extension overloads *before* `build`/`configure` runs. Order is now `context`/`set` → `configure` → `options` → user callback. Also found and fixed a related defect: `DbContext.BulkExecuteAsync(build, options, ct)` was declared `async`, so **every** exception in it (including the pre-existing `context`/`build` guards) was captured into the returned Task instead of thrown at the call site. Dropped the `async`/`await` (it only added a state machine — `ExecuteAsync` already returns a Task), which makes validation synchronous and consistent with the other five overloads. Covered by `ArgumentValidationTests`, including a guard that the callback still runs when options are valid.

---

## P8 — XML docs on public API surface

**Why:** `GenerateDocumentationFile=true` but `NoWarn 1591` (`Directory.Build.props:17`) means ~90% of public members have zero XML docs — IntelliSense shows signatures only. Critical contracts (no implicit transaction, options = full replacement, batch not thread-safe) live only in README/DESIGN.

**Work:**
- Remove `1591` from `NoWarn` (or scope it) and add `/// <summary>/<remarks>/<param>/<returns>/<exception>` to:
  - `IBulkBatch` (all members, including thread-safety remark on the interface)
  - `BulkBatch` (consider whether the concrete public type should be documented as "prefer `CreateBulkBatch()`")
  - All 6 `BulkBatchExtensions` methods + `TableUpdateBuilder`/`TableUpsertBuilder`
  - All builders in `BulkOperationBuilders.cs` (esp. `UpdateWhen` semantics: guards matched update only, never insert)
  - `BulkExecuteOptions` (all 3 props, `Clone`, `CopyTo`) — document **full-replacement** semantics on per-call overloads
  - `BulkExecuteResult`, `OperationResult`, `BulkZeroRowsAffectedException`
  - All 4 `UseBulkExecute` overloads
  - `BulkExecuteOptionsExtension`
- Ensure `<exception>` tags for `ArgumentNullException`, `ArgumentOutOfRangeException`, `BulkZeroRowsAffectedException`, `NotSupportedException` (missing provider), `InvalidOperationException` (missing Where/Set).

**Files:**
- All public `.cs` files under `src/` listed above.
- `Directory.Build.props` (NoWarn change).

**Acceptance:**
- `1591` warnings = 0 in Release build.
- Key contracts (no implicit transaction; options full-replacement; not thread-safe) visible in IntelliSense on the relevant members.

**Depends on:** none (parallelizable; large but mechanical)

---

## P9 — Add `DbSet.BulkDeleteAsync` sugar + expose entity-row delete

**Why:** `TableUpdateBuilder` and `TableUpsertBuilder` exist but there is no delete equivalent — users must drop to `db.BulkExecuteAsync(b => b.Delete<T>(...))`. Separately, entity-row `Delete(IEnumerable<TEntity>)` plumbing already exists: `BindEntityRows(..., bool update)` branches `update ? Update : Delete` (`BulkBatch.cs:562,590`) but every public caller passes `update: true` — the delete branch is dead code.

**Work:**
1. Add `TableDeleteBuilder<TEntity>` with `Add(Action<DeleteOperationBuilder<TEntity>>)`.
2. Add `DbSet<TEntity>.BulkDeleteAsync(Action<TableDeleteBuilder<TEntity>> configure, ...)` (+ options overload) in `BulkBatchExtensions.cs`, mirroring `BulkUpdateAsync` exactly (including P6 null validation).
3. Expose entity-row delete: `IBulkBatch.Delete<TEntity>(IEnumerable<TEntity> rows)` (PK match) — routes to `BindEntityRows(..., update: false)`; update `IBulkBatch` (see P13 note: interface change is a binary-compat decision — add now, before 1.x grows dependents, or add as extension method if preserving the interface is preferred).
4. Golden-SQL + integration tests for all three providers.

**Files:**
- `src/NSLabs.EFCore.Extensions/IBulkBatch.cs`
- `src/NSLabs.EFCore.Extensions/BulkBatch.cs`
- `src/NSLabs.EFCore.Extensions/BulkBatchExtensions.cs`
- Tests: unit + integration per provider.

**Acceptance:**
- `db.AuditLogs.BulkDeleteAsync(b => b.Add(op => op.Where(...)))` works and matches batch-API SQL.
- Entity-row delete produces PK-match delete SQL identical to hand-written batch delete.
- P3 fast path (if landed) applies to entity-row delete too.

**Depends on:** P13 decision (interface member vs extension); P6 for consistent validation pattern.

---

## P10 — Unify builder verb naming

**Why:** Three verbs for column assignment across builders: `Set` (update builder), `Update` (upsert builder), `SetProperty` (alias on both, expression overload only). `UpsertOperationBuilder.Update` also collides conceptually with `IBulkBatch.Update` (whole operation).

**Work:**
- Decide canonical verbs:
  - Recommendation: keep `Set` as primary on **both** Update and Upsert builders; keep `Update` and `SetProperty` as `[Obsolete]` aliases on upsert (or keep `Update` as EF-familiar alias — team decision).
  - `IBulkBatch.Update/Upsert/Delete` stay as operation-level verbs.
- Apply consistently; add XML docs clarifying operation-level vs column-level verbs.
- No breaking removal in a minor release — obsolete first.

**Files:**
- `src/NSLabs.EFCore.Extensions/BulkOperationBuilders.cs`
- Samples + README if verb guidance changes.

**Acceptance:**
- One documented primary column-assignment verb per builder; aliases obsolete with clear message.
- Existing code still compiles (warnings only).

**Depends on:** none (decision needed before implementation)

---

## P11 — Fix `AddNSLabsBulkInstrumentation` DI semantics

**Why:** The method name implies a DI registration but it only mutates process-global static state (`BulkInstrumentationServiceExtensions.cs:16-28`). Two hosts/isolates in one process share one policy; tests must call `BulkInstrumentation.Reset()`.

**Work:**
- Option A (preferred): make it a real registration — `IOptions`-style or a singleton `BulkInstrumentationOptions` resolved via DI, with `BulkInstrumentation.Configure` remaining for back-compat.
- Option B (smaller): keep static but rename/mark docs clearly ("process-wide; not a container registration") and add per-`DbContext` / per-call override capability via `BulkExecuteOptions` or a new `Instrumentation` property.
- Whichever: preserve existing `BulkInstrumentation.Configure/Current/Reset` API (back-compat); add tests for the DI path (currently zero).

**Files:**
- `src/NSLabs.EFCore.Extensions/DependencyInjection/BulkInstrumentationServiceExtensions.cs`
- `src/NSLabs.EFCore.Extensions/BulkInstrumentation.cs`
- `src/NSLabs.EFCore.Extensions/BulkInstrumentationOptions.cs`
- Tests: new DI test.

**Acceptance:**
- Behavior of process-wide static preserved for existing callers.
- New path either registers a real service or is unambiguously documented as global config.
- Test covers `AddNSLabsBulkInstrumentation`.

**Depends on:** decision A vs B

---

## P12 — Emit `BulkExecuteRetrying` (60005) log event

**Why:** Event ID is defined and documented as stable (`BulkEventId.cs:49-50`, doc: "Execution-strategy retry attempt (Phase 2)") but no `[LoggerMessage]` method exists — retries only produce span events (`RecordRetryAttempt`). Anyone filtering logs by 60005 gets nothing.

**Work:**
- Add `[LoggerMessage(BulkEventId.BulkExecuteRetrying, ...)]` method to `BulkExecuteLoggingDefinitions.cs` (Warning level, matches retry semantics).
- Emit from all three executors where `strategy.RetriesOnFailure` branch handles retry (`SqlServerExecutor.cs:38` area, Npgsql/Sqlite equivalents) — alongside the existing span event.
- Hoist `IsEnabled` guard per existing pattern (`BulkBatch.cs:162-163` style).
- Test: exercise retry path (ties into P14 retry tests) and assert the event ID fires.

**Files:**
- `src/NSLabs.EFCore.Extensions/Diagnostics/BulkExecuteLoggingDefinitions.cs`
- `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs`
- `src/NSLabs.EFCore.Extensions.Npgsql/Internal/NpgsqlExecutor.cs`
- `src/NSLabs.EFCore.Extensions.Sqlite/Internal/SqliteExecutor.cs`
- Tests: logging test class (Sqlite has `BulkExecuteLoggingTests`).

**Acceptance:**
- 60005 emitted on execution-strategy retry; existing event IDs unchanged.
- No log emission when level disabled (IsEnabled guard).

**Depends on:** P14 (retry test infrastructure) — can implement event first, test lands with P14.

---

## P13 — API baseline tooling (PublicAPI/ApiCompat)

**Why:** No `PublicAPI.Shipped.txt`, no ApiCompat — binary compatibility relies on manual review. Known landmines: `IBulkBatch` has no default interface members (any added member is a binary break), `OperationResult` is a positional record, `BulkExecuteResult` uses `required` init props, `BulkBatch` exposes a public primary constructor.

**Work:**
- Enable `Microsoft.CodeAnalysis.PublicApiAnalyzers` (or `EnablePackageValidation` + ApiCompat in the SDK) across the four `src` projects.
- Generate and commit initial `PublicAPI.Unshipped.txt` / `PublicAPI.Shipped.txt` for the current 1.0.0 surface.
- Add a CI step that fails on unshipped API changes without an entry (or on breaking diffs vs the previous package baseline).
- Document the process for intentionally shipping a new API (move Unshipped → Shipped at release).

**Files:**
- `Directory.Build.props` or per-project csproj (analyzer package + settings).
- New: `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` per src project.
- `.github/workflows/build.yml` (optional CI gate).

**Acceptance:**
- Build fails if a public member changes without updating the baseline file.
- Release workflow validates against the prior package version.

**Depends on:** none. **Do this before P9/P10/P11 land** so interface/record changes are caught deliberately.

---

## P14 — Test gaps: retry, registry, re-execution, concurrency, DI

**Why:** Several shipped code paths have zero test coverage; a few are new fixes from this plan.

**Work:**
| Gap | Target |
|-----|--------|
| Execution-strategy retry (`RetriesOnFailure` branch) untested in all 3 executors | New unit tests with a fake/retrying strategy; assert `retry.attempt` span + (after P12) log 60005 |
| `BulkProviderRegistry.TryLoad` reflection fallback + missing-provider message untested | Unit test forcing fallback; assert `NotSupportedException` remediation text |
| Batch re-execution (`ExecuteAsync` twice) promised in DESIGN.md:112, untested | Test re-execute incl. after `ThrowIfZeroAffected` failure |
| **No concurrency tests** | Parallel `ExecuteAsync`, parallel `BulkInstrumentation.Configure`, parallel registry `Resolve` (validates P5) |
| `AddNSLabsBulkInstrumentation` untested | ServiceCollection registration + behavior test (with P11) |
| Argument-validation gaps untested | `ArgumentNullException` facts for DbSet overloads (locks in P6) |
| Options-null ordering untested | Callback-not-invoked fact (locks in P7) |
| Entity-row `Delete` unreachable/untested | Tests accompany P9 |
| `chunk.skipped` event path untested | Sqlite/Npgsql zero-row upsert no-op |
| Instrumentation failure-isolation untested | Throwing logger/listener cannot fail a batch (`BulkBatch.cs:141-157` guards) |
| Thin areas: `EntityStyleExecutionTests` (2 facts), `MixedAndEntityRowTests` (3 facts) | Expand coverage for entity-row + mixed batching |

**Files:**
- All `tests/` unit projects; shared helpers in `Tests.Unit.Shared/FakeAdo.cs` as needed.

**Acceptance:**
- All new facts green across all three provider test projects.
- CI runs them (already does via `dotnet test --solution`).

**Depends on:** P5, P6, P7, P12, P9 (each test lands with or after its fix)

**Status:** IN PROGRESS — three rows landed with the Wave 0 fixes: parallel registry `Resolve`/`Register` (validates P5), `ArgumentNullException` facts for the DbSet overloads (locks in P6), and the callback-not-invoked ordering facts (locks in P7). Remaining: execution-strategy retry, `TryLoad` missing-provider message, batch re-execution, parallel `ExecuteAsync` / `BulkInstrumentation.Configure`, `AddNSLabsBulkInstrumentation`, `chunk.skipped`, instrumentation failure-isolation, and the thin `EntityStyleExecutionTests` / `MixedAndEntityRowTests` areas.

---

## P15 — Unify logging/PII verbosity gates

**Why:** Chunk SQL text is logged at Information **unconditionally** when a logger factory exists (`BulkExecuteLoggingDefinitions.cs:52`), but `BulkInstrumentationOptions.CaptureCommandText` only controls span attributes. Three knobs (`LogFragment`, `CaptureCommandText`, log level) govern two subsystems with different defaults — confusing and a potential PII inconsistency.

**Work:**
- Decide: should `CaptureCommandText` (or a new option) gate **both** span `db.statement` **and** chunk log SQL?
  - Recommendation: yes — one PII switch. Log level still applies as an outer gate.
- If gating logs: pass the instrumentation snapshot into the chunk-log emission path (it already resolves a `TelemetryContext` when tracing — extend or share that resolution for logging).
- Document the single source of truth (category level + `CaptureCommandText`).

**Files:**
- `src/NSLabs.EFCore.Extensions/Diagnostics/BulkExecuteLoggingDefinitions.cs`
- `src/NSLabs.EFCore.Extensions/BulkBatch.cs` (log emission block ~`:162-216`)
- `src/NSLabs.EFCore.Extensions/BulkInstrumentationOptions.cs`
- Tests: `BulkExecuteLoggingTests` (assert SQL omitted when capture disabled).

**Acceptance:**
- One option controls command-text visibility across logs and spans.
- Default remains PII-safe (capture off).

**Depends on:** P11 decision (instrumentation config channel)

---

## P16 — Micro-allocations & polish

Smaller performance items — batch into one PR after P1-P4 land (or fold into P4).

| # | Item | Location |
|---|------|----------|
| a | `PartitionNulls` always allocates a list; IN emission does up to 3 passes (`NonNullCount` + `IsByteArrayElementList` + `PartitionNulls`) | `LargeListHelper.cs:15-47,52-63`; call sites in all 3 generators |
| b | `List<object?>` without capacity for IN values | `LinqPredicateTranslator.cs:247,272` |
| c | `HashSet<int>` without capacity in `GetDistinctIndices` | `SqlServerSqlGenerator.cs:207` (and Npgsql/Sqlite equivalents) |
| d | `BuildChunk` param estimate hardcoded `* 3` ignores real predicate/SET counts → list regrowth; use existing `CountDecidedTotal` | `SqlServerSqlGenerator.cs:150-157` |
| e | `UpsertKey` per-row heap object; `GetHashCode` uses interface enumerator; `Equals` uses indexed loop (inconsistent); shape key uses unpooled `new StringBuilder()` | `BulkBatch.cs:290,305,322-347` |
| f | `BindConflict` uses `Select().ToList()` (breaks manual-loop discipline) | `BulkBatch.cs:638-644` |
| g | `bindableList.ToArray()` extra copy | `BulkBatch.cs:586` |
| h | Dead LINQ method `OperationIndicesOf` unused | `SqlServerSqlGenerator.cs:219-220` |
| i | Static metadata caches (`ModelBinder` table/column/getter dicts) unbounded across model rebuilds | `ModelBinder.cs:12-18,26-37` — consider bounded cache or model-key invalidation note |
| j | `BulkInstrumentation.SnapshotRef` takes a lock per traced batch — consider volatile read of immutable snapshot | `BulkInstrumentation.cs:41-46` |

**Acceptance:** existing tests pass; benchmark allocations drop where applicable; dead code removed.

**Depends on:** P1 (measure), ideally after P2/P4 to avoid conflicts.

---

## Recommended sequencing

```
Wave 0 — foundation & safety (can all land in parallel, immediately)
  P1  Benchmarks (baseline)
  P5  Registry ConcurrentDictionary
  P6  DbSet null validation
  P7  Options-before-callback validation
  P13 API baseline tooling   ← before any public surface changes (P9/P10/P11)

Wave 1 — top performance wins
  P2  Expression compile caching          (measure with P1)
  P3  Entity-row match fast path          (measure with P1)

Wave 2 — larger refactors
  P4  StringBuilder-direct SQL emission
  P16 Micro-allocations (fold into or follow P4)

Wave 3 — usability
  P8  XML docs (mechanical; parallelizable anytime after Wave 0)
  P9  BulkDeleteAsync + entity-row delete (after P13)
  P10 Verb naming unification (decision first)
  P11 Instrumentation DI (decision first)
  P12 Retry log event
  P15 Logging/PII gate unification (after P11 decision)

Wave 4 — test hardening
  P14 Test gaps (follows each fix; final sweep for retry/concurrency/re-execution)
```

### Decision points (need answers before implementation)

1. **P10:** Canonical column-assignment verb — `Set` everywhere (obsolete `Update`/`SetProperty`), or keep EF-familiar `Update` on upserts?
2. **P11:** Option A (real DI registration) vs Option B (keep static + per-call override + rename/docs)?
3. **P15:** Should `CaptureCommandText` gate chunk **logs** as well as spans? (Recommended: yes.)
4. **P9:** Add `IBulkBatch.Delete(rows)` as an interface member (binary-compat event — do early, post-P13) or as an extension method only?
