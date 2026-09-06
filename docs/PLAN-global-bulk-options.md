# Plan: Per-DbContext `BulkExecuteOptions` via `UseBulkExecute` (Zero-Config Preserved)

> Status: **FINALIZED — per-DbContext DI only (`UseBulkExecute`). No static fallback.**
> Goal: configure `BulkExecuteOptions` **once per `DbContext` type** via
> `AddDbContext<T>(o => o.UseBulkExecute(...))`; every `ExecuteAsync` /
> `BulkExecuteAsync` / `BulkUpdateAsync` / `BulkUpsertAsync` on that context uses it automatically.
> Per-call override = pass an explicit `BulkExecuteOptions` object (full replacement, today's overloads).
> No static global, no `AddBulkExtensions`, no lambda-patch overloads.
> Zero-configuration preserved: never call `UseBulkExecute` → `new BulkExecuteOptions()` factory
> defaults, exactly as today. No code changed yet.

---

## 1. Problem statement

Today every entry point requires the caller to pass `BulkExecuteOptions` explicitly
or it falls back to `new BulkExecuteOptions()`:

```csharp
// Current — repeats on every call if you want non-defaults
await db.BulkExecuteAsync(b => { ... }, new BulkExecuteOptions
{
    MaxParametersPerCommand = 4,
    CommandTimeout = 30,
    ThrowIfZeroAffected = true,
});

var r = await batch.ExecuteAsync(new BulkExecuteOptions { ThrowIfZeroAffected = true });
```

Finalized request:

1. **Set up options once per `DbContext` type** via `UseBulkExecute`, reuse automatically on every `ExecuteAsync`.
2. **Override per call** by passing an explicit `BulkExecuteOptions` object (full replacement).
3. **No loss of zero-config**: library works with no setup at all (current defaults unchanged).

---

## 2. Constraints / non-goals (must not break)

| # | Constraint | Why it matters |
|---|---|---|
| C1 | Zero-config preserved: `db.BulkExecuteAsync(b => ...)` and `batch.ExecuteAsync()` work with no setup | Core library promise; samples (`UseSqlite`, file DB), all existing tests rely on it |
| C2 | No breaking change to existing public overloads / defaults (except the deliberate `OnCommandText` removal, §5.8) | `BulkExecuteOptions` defaults today: `MaxParametersPerCommand=2000`, `ThrowIfZeroAffected=false`, `CommandTimeout=null` (see `TransactionTests.BulkExecuteOptions_has_expected_options`). Must stay identical |
| C3 | No implicit transaction / execution semantics change | Per-context options only affect chunking/timeout/logging/zero-check, never transaction creation |
| C4 | Thread-safe, pooling-safe, no shared-mutable-state bugs | Extension instance is shared across pooled contexts/threads |
| C5 | No per-call log delegate on the options | Logging flows through EF's `ILoggerFactory` (see §5.9); no delegate lifetime/leak semantics to define |
| C6 | Provider layer (`IBulkProvider`, `SqlServerExecutor`, `SqliteExecutor`, `NpgsqlExecutor`) unchanged in behavior | They already take a resolved `BulkExecuteOptions`; only resolution site changes |

Out of scope for v1: per-entity-type options, staging-table fast path, change-tracking flags,
retry-policy options. The design does not block adding them later.

---

## 3. Current state (verified in repo)

Entry points that construct defaults today:

| File | Code |
|---|---|
| `src/NSLabs.EFCore.Extensions/BulkBatch.cs:56-57` | `ExecuteAsync(ct) => ExecuteAsync(new BulkExecuteOptions(), ct)` |
| `src/NSLabs.EFCore.Extensions/BulkBatchExtensions.cs:16-20` | `BulkExecuteAsync(build, ct) => BulkExecuteAsync(build, new BulkExecuteOptions(), ct)` |
| `src/NSLabs.EFCore.Extensions/BulkBatchExtensions.cs:36-40, 53-57` | `BulkUpdateAsync` / `BulkUpsertAsync` same pattern |
| `src/NSLabs.EFCore.Extensions/BulkExecuteOptions.cs` | Mutable sealed class, 4 settable props, no `Clone()`, no validation |
| `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:65,113,126` (same in Sqlite/Npgsql executors) | Reads `options.ThrowIfZeroAffected`, `options.CommandTimeout`, invokes `options.OnCommandText` per chunk (line to be deleted; `ILogger` follow-up in §5.8); chunking uses `options.MaxParametersPerCommand` via `provider.Generate(...)` in `BulkBatch.ExecuteAsync` |
| `IBulkBatch` | Exposes only the two `ExecuteAsync` overloads |

Resolution timing today: options object is created at the **call site** (`BulkBatchExtensions`)
and passed straight into `BulkBatch.ExecuteAsync(options, ct)` → `provider.Generate(...,
options.MaxParametersPerCommand)` → `provider.ExecuteAsync(..., options, ...)`.

---

## 4. Final design (no alternatives — decided)

**Chosen: per-DbContext `UseBulkExecute` + explicit-object per-call override. Nothing else.**

```csharp
builder.Services.AddDbContext<OrdersDbContext>(o => o
    .UseSqlServer(ordersCs)
    .UseBulkExecute(b => { b.ThrowIfZeroAffected = true; b.CommandTimeout = 30; }));
builder.Services.AddDbContext<AuditDbContext>(o => o.UseSqlServer(auditCs)); // factory defaults

await ordersDb.BulkExecuteAsync(b => { ... }); // uses OrdersDbContext snapshot
await auditDb.BulkExecuteAsync(b => { ... });  // factory defaults — isolated
await ordersDb.BulkExecuteAsync(b => { ... }, new BulkExecuteOptions { CommandTimeout = 60 }); // override
```

Why this shape (EF Core gotcha): `DbContext` resolves from its **internal service provider**
built from `DbContextOptions.Extensions`, not the root `IServiceProvider`. So plain
`services.Configure<BulkExecuteOptions>(...)` is **invisible** to `db.BulkExecuteAsync(...)`.
The EF-idiomatic fix — used by Npgsql options, `EFCore.NamingConventions`, interceptors — is
`optionsBuilder.UseX(...)` + `IDbContextOptionsExtension`, resolved at execution via
`context.GetService<IDbContextOptions>()?.FindExtension<...>()`. That is what this plan implements.

Rejected and out of scope (do not implement):

- Static process-wide fallback (`BulkExecuteDefaults`) — rejected: cross-context sharing, test
  pollution, extra surface. Isolation per `DbContext` type is the requirement.
- `services.AddBulkExtensions` bridge / root `IOptions<>` wiring — rejected: execution cannot see
  the root container; any such sugar would be misleading. Users needing `IConfiguration` bind it
  inside `UseBulkExecute` (see §5.7).
- `Action<BulkExecuteOptions>` lambda-patch overloads / `CreateBulkBatch(configure)` — rejected:
  patch-on-shared-state is what forced a per-execution clone; explicit-object replacement already
  covers "override for the specific use case" with zero new surface.
- Per-instance bags (`ConditionalWeakTable`, `db.SetBulkDefaults`) — rejected: pooling/lifetime hazards.

Effective resolution (only two layers + factory):

```
per-call BulkExecuteOptions object (if supplied, full replacement)
else UseBulkExecute snapshot for that DbContext type (if present)
else factory defaults (new BulkExecuteOptions())
```

---

## 5. API

### 5.1 Cloning: what is required (read before optimizing)

No static and no patch overloads — so **no per-execution clone on the hot path**. Remaining copies:

| Boundary | Clone? | Why |
|---|---|---|
| `UseBulkExecute` registration (copy-in) | **YES — once per startup, zero per-execution cost** | The `Action<BulkExecuteOptions>` receives a caller-visible instance. The caller can capture it (`BulkExecuteOptions? captured; ...UseBulkExecute(o => captured = o)`) and mutate it later, corrupting the shared extension for all pooled contexts/threads. Apply-action-to-temp → `Validate()` → store a private copy. Non-negotiable. |
| `ExecuteAsync` default path (DI snapshot → execution) | **NO clone — direct read-only use of the shared reference** | Execution never mutates the options (`Generate` reads `MaxParametersPerCommand`; executors read `CommandTimeout` / `ThrowIfZeroAffected`; `Validate()` is read-only). Concurrent reads of a never-mutated reference are safe. Hot path stays allocation-free. |
| `ExecuteAsync` explicit-object path | **YES — one tiny clone per overriding call (~32 bytes)** | Without it, `var t = db.BulkExecuteAsync(b, o); o.CommandTimeout = 999; await t;` changes in-flight behavior (timeout is read later in the executor). Skipping it under a "don't mutate during `await`" contract saves nanoseconds vs milliseconds of DB I/O — keep the clone. |
| Public `BulkExecuteOptionsExtension.Options` getter | **Return a clone (user access is rare; execution uses the internal field)** | The extension is shared. Exposing the live mutable reference lets anyone do `FindExtension(...).Options.X = ...` and corrupt all futures. Execution reads the private field directly (no clone); only the public getter clones. |

Cost reality: one `Clone()` = one Gen0 alloc + 4 field copies. The default path does **zero** clones.
Do not remove the two remaining copies — they save nothing observable and reintroduce aliasing bugs.

> ### Why the class stays mutable (not `record` + `init`)
>
> Sharing would be safe with immutability, but it breaks existing post-construction mutation (C2):
> ```csharp
> var o = new BulkExecuteOptions { MaxParametersPerCommand = 4 };
> o.CommandTimeout = 30;          // CS8852 with init
> void Tweak(BulkExecuteOptions x) { x.ThrowIfZeroAffected = true; } // same break
> ```
> `class` → `record` also changes equality/`GetHashCode`/contract (source- and binary-breaking).
> Future path if ever wanted: *add* `With*` helpers alongside setters, never replace them.

### 5.2 `BulkExecuteOptions` additions (same file, non-breaking)

```csharp
public sealed class BulkExecuteOptions
{
    public int MaxParametersPerCommand { get; set; } = 2000;
    public bool ThrowIfZeroAffected { get; set; }
    public int? CommandTimeout { get; set; }

    public BulkExecuteOptions Clone(); // copies 3 props
    public void CopyTo(BulkExecuteOptions target); // for IConfiguration bridging
    internal void Validate();          // throws ArgumentOutOfRangeException on bad values
}
```

No default-value or nullability changes → C2 preserved.
`OnCommandText` (`Action<string>?`) is **removed** (breaking — see §5.8 migration):
logging flows through EF's `ILoggerFactory` resolved from the `DbContext`, not a delegate
on the options. `Clone()` drops from 4 field copies to 3.

### 5.3 Resolution rule

> **No-arg call = that `DbContext` type's `UseBulkExecute` snapshot, else factory defaults.**
> **Explicit `BulkExecuteOptions` object = full replacement (back-compat, DI ignored).**
> No merge, no patch.

Full-replacement (not merge) because a mutable bag cannot distinguish "left `false` by default"
from "explicitly turning the DI `true` off for this call". Merge heuristics would surprise; full
replacement keeps every existing call identical.

| Call shape | Effective options |
|---|---|
| `ExecuteAsync(ct)` / `BulkExecuteAsync(build)` | `FindExtension` snapshot if that context type called `UseBulkExecute`, else `new BulkExecuteOptions()` |
| `ExecuteAsync(options, ct)` (object) | `options` (cloned on entry, validated; DI ignored) |
| Nothing configured anywhere | `new BulkExecuteOptions()` → identical to today |

Resolve **at execution time** (inside `ExecuteAsync`), not at `CreateBulkBatch` time. Fail fast in
`UseBulkExecute`; re-validate read-only at execution.

### 5.4 How `UseBulkExecute` is resolved (no magic)

```
1. Startup:  AddDbContext<SampleDbContext>(o => o.UseSqlite(cs).UseBulkExecute(b => ...))
                └─> UseBulkExecute applies Action to a temp, Validate(), stores a private COPY
                    in a new BulkExecuteOptionsExtension via
                    ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(ext).
                    Never stores the caller's live instance.

2. EF Core:  DbContextOptions<SampleDbContext> now carries the extension.
             Contexts without the call carry none → isolated per context type.

3. Request:  scope → SampleDbContext built by internal provider.
             BulkBatch only holds the DbContext instance (as today).

4. Execute:  await db.BulkExecuteAsync(b => {...})
                └─> ResolveEffective(context, explicitOptions):
                    - explicit object? → Clone() + Validate() → use (DI ignored)
                    - else internal extension field reference (read-only, no clone)
                      ?? new BulkExecuteOptions()
```

Why `FindExtension`, not `context.GetService<BulkExecuteOptions>()`: `ApplyServices` is a **no-op**
by design. Registering options as an internal singleton would require
`GetServiceProviderHashCode`/`ShouldUseSameServiceProvider` to vary with values, otherwise
`AddDbContextPool` could share a stale singleton. `FindExtension` avoids that bug class.
`IDbContextOptions` is always available via `context.GetService<>()` once constructed —
safe inside `ExecuteAsync`. (Same channel also yields the `ILoggerFactory` for §5.9.)

### 5.5 Overloads — no changes

```csharp
// IBulkBatch (unchanged):
Task<BulkExecuteResult> ExecuteAsync(CancellationToken ct = default);                        // → DI snapshot else factory
Task<BulkExecuteResult> ExecuteAsync(BulkExecuteOptions options, CancellationToken ct = default); // → full replace

// BulkBatchExtensions (unchanged signatures; no-arg path now resolves DI instead of `new`):
BulkExecuteAsync(build, ct) / BulkUpdateAsync(configure, ct) / BulkUpsertAsync(configure, ct)
BulkExecuteAsync(build, BulkExecuteOptions, ct) // etc.
```

### 5.6 Usage

```csharp
// Startup — once per DbContext type:
builder.Services.AddDbContext<OrdersDbContext>(o => o
    .UseSqlServer(ordersCs)
    .UseBulkExecute(b => { b.ThrowIfZeroAffected = true; b.CommandTimeout = 30; }));
builder.Services.AddDbContext<AuditDbContext>(o => o.UseSqlServer(auditCs)); // defaults

await ordersDb.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "V1"));
}); // DI snapshot

await ordersDb.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "V1"));
}, new BulkExecuteOptions { CommandTimeout = 60 }); // full replacement for this call

IBulkBatch batch = ordersDb.CreateBulkBatch();
batch.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "V1"));
var r = await batch.ExecuteAsync(); // ← resolves at execution time
```

### 5.7 Host wiring — `UseBulkExecute` only

New files under `src/NSLabs.EFCore.Extensions/DependencyInjection/`:

- `BulkExecuteOptionsExtension.cs` (`IDbContextOptionsExtension` + `DbContextOptionsExtensionInfo`;
  private cloned snapshot; `Validate()` fail-fast; `ApplyServices` = no-op; public `Options` getter
  returns a clone, execution uses the internal field).
- `BulkExecuteBuilderExtensions.cs` (`UseBulkExecute` on `DbContextOptionsBuilder` +
  generic `DbContextOptionsBuilder<TContext>` + instance overload; clone-apply-validate-`AddOrUpdateExtension`).

```csharp
builder.Services.AddDbContext<SampleDbContext>(options => options
    .UseSqlite(connectionString)
    .UseBulkExecute(o => { o.ThrowIfZeroAffected = true; o.CommandTimeout = 30; }));

// IConfiguration binding (all 3 props are bindable scalars — no delegate left):
builder.Services.AddDbContext<SampleDbContext>((sp, options) =>
{
    var bulkSection = sp.GetRequiredService<IConfiguration>().GetSection("Bulk");
    options.UseSqlite(connectionString).UseBulkExecute(o => bulkSection.Bind(o));
});
```

Rules:

1. Per-`DbContext`-type. Types without the call use factory defaults.
2. Root `IOptions<>` alone never affects execution (internal vs root provider).
3. `AddDbContextPool` safe: immutable extension, no-op `ApplyServices`.
4. No setup → behavior == today (including logging: no `LogTo`/provider configured → silence).

### 5.8 `OnCommandText` removal now; `ILogger` later (deferred)

Decision (locked): **delete `OnCommandText: Action<string>?` in this change; ship `ILogger`
support as a follow-up.** Until the follow-up lands there is no per-chunk SQL tap — accepted
window, recorded here so it isn't forgotten. Rationale for the end state:

- EF Core and the neighbouring bulk libs (`EFCore.BulkExtensions`, `FlexLabs.Upsert`) expose **no**
  per-call `Action<string>` SQL hook. EF's surface is `LogTo` / `ILogger` integration /
  `IDbCommandInterceptor` / `DiagnosticSource` — levels, filtering, scopes, and sinks for free.
- Our executors build a raw `DbCommand` over `GetDbConnection()`, bypassing EF's
  relational-command pipeline, so today `LogTo`/interceptors never fire for bulk batches and the
  delegate was the only tap. Emitting through the context's `ILoggerFactory` (follow-up) closes
  that gap with zero new concepts for EF users and zero delegate-lifetime hazards on the options.
- Core package impact at that time is minimal: one explicit `PackageReference` to
  `Microsoft.Extensions.Logging.Abstractions` (already centrally pinned at 10.0.0; transitively
  present today via `Microsoft.EntityFrameworkCore.Relational`). No OTel/SDK packages.

Deferred `ILogger` sketch (do not implement in this change — retained so the follow-up starts here):

```csharp
// NEW Internal/BulkLogging.cs — category + EventIds + LoggerMessage wrappers:
internal static class BulkLogging
{
    public const string Category = "NSLabs.EFCore.Extensions.Bulk"; // filterable independently
    public static readonly EventId BatchExecuted = new(31001, nameof(BatchExecuted));
    public static readonly EventId ChunkExecuting = new(31002, nameof(ChunkExecuting)); // Debug
    // LoggerMessage.Define(...) delegates with IsEnabled guards — no string alloc when disabled.
}
```

- Resolution: `context.GetService<ILoggerFactory>()?.CreateLogger(Category)` inside `ExecuteAsync`
  (EF registers a `NullLoggerFactory` fallback; guard anyway). No logger on the options, nothing to
  clone, nothing to capture — pooling-safe by construction.
- What is logged: per-chunk `CommandText` (parameterized placeholders only, never parameter values)
  at `Debug`, plus a one-line batch summary (operations, chunks, `TotalRowsAffected`, provider) at
  `Information`, so default `LogTo(Console, Information)` shows one line per batch, full SQL on `Debug`.
  Document the filter recipe:
  `options.LogTo(Console.WriteLine, LogLevel.Debug)` or
  `options.LogTo(..., (id, level) => level >= Information || id.Id is 31001 or 31002)`.
- Performance: every emit site checks `logger.IsEnabled(level)` **before** touching `CommandText`;
  `LoggerMessage` pattern, no interpolation when disabled. Disabled logging ≈ one interface call per
  chunk — same order as the removed null-delegate check.
- Sensitive data: `CommandText` holds no literal values (all parameters); never log
  `chunk.Parameters` values. No `EnableSensitiveDataLogging` coupling needed.
- Errors: exceptions propagate as today; log the failure at `Error` with the EventId before rethrow
  (including `BulkZeroRowsAffectedException`). Empty batch → no log.

Migration for this change (breaking — call it out in release notes):

- `BulkExecuteOptions.OnCommandText` is **deleted**. Temporary compat (one minor, if the package is
  already 1.x): keep it `[Obsolete("Bulk SQL logging moved to standard EF Core logging — see follow-up.")]`
  as a no-op (invoke nothing; `ILogger` not yet wired) — then remove. If still pre-1.0, delete outright.
- Tests in this change: delete `logged.Add` hook capture; assert chunk counts via `Harness.Generate` /
  chunk plans or `FakeAdo` executed commands instead (no logger yet to assert against). Samples:
  `Example4` drops its `OnCommandText` capture (chunk-count via the returned result / reworked sample
  when `ILogger` lands).

---

## 6. Implementation steps

- [ ] **1. `src/NSLabs.EFCore.Extensions/BulkExecuteOptions.cs`** — delete `OnCommandText` (or no-op `[Obsolete]` bridge for one minor if 1.x); add `Clone()` + `CopyTo` + `Validate()`. No other default changes.
- [ ] **2. NEW DI files** — extension (private clone, no-op `ApplyServices`, cloning public getter) + `UseBulkExecute` overloads (`BulkExecuteBuilderExtensions`). No `Microsoft.Extensions.Options` reference needed.
- [ ] **3. `src/NSLabs.EFCore.Extensions/BulkBatch.cs`** — `ResolveEffective(context, explicitOptions)`; explicit → clone + validate; else internal field ref (no clone) else `new`; empty-batch fast path unchanged (no log).
- [ ] **4. `BulkBatchExtensions` + `IBulkBatch`** — no signature changes; no-arg paths use `ResolveEffective`.
- [ ] **5. All three `*Executor.ExecuteChunkAsync`** — delete the `options.OnCommandText?.Invoke(...)` line. No logger wiring in this change (deferred to the `ILogger` follow-up, §5.8).
- [ ] **6. Tests** (see §8). **7. Docs/samples** (see §9).

---

## 7. Edge cases

| # | Edge | Decision |
|---|---|---|
| E1 | Shared DI instance mutated mid-execution | Impossible by construction: copy-in at registration + cloning public getter; execution never writes |
| E2 | Explicit per-call object mutated mid-`await` | Cloned on entry (only on override calls) |
| E3 | Invalid values in `UseBulkExecute` | Throw at registration + read-only re-validate at execution |
| E4 | Per-call vs DI snapshot | Object overload replaces snapshot entirely (no merging of individual props) |
| E5 | `MaxParametersPerCommand` too high for SQL Server | Allowed (PG/Sqlite allow more); generator error unchanged |
| E6 | Empty batch with `ThrowIfZeroAffected=true` | Still `Empty`, no throw, no log |
| E7 | Logging action throwing | N/A anymore (no callback); `ILogger` providers should never throw — a throwing provider is the host's bug, not ours |
| E8 | Public `Options` getter abused | Returns clone; execution uses internal field — test this |
| E9 | `CreateBulkBatch()` then `ExecuteAsync()` | Resolves at execution time |
| E10 | DI-created vs `new DbContext()` | Extension missing → factory defaults; both work. `ILoggerFactory` resolves in both (Null fallback) |
| E11 | Logger provider capturing scoped services | Standard EF logging rules apply; nothing bulk-specific stored |
| E12 | Two context types, one configured | Other → factory defaults (test isolation) |
| E13 | `AddDbContextPool` | Safe: immutable extension + no-op services |
| E14 | `UseBulkExecute` twice | Last `AddOrUpdateExtension` wins; additive from first clone, validated |
| E15 | No logging configured (`LogTo` never called) | Silence (guarded `IsEnabled`); identical perf profile to removed null-delegate check |

---

## 8. Test plan

Unit (no DB):

- [ ] Factory defaults unchanged (`2000`, `false`, `null`) and no `OnCommandText` member (or `[Obsolete]` bridge only)
- [ ] `UseBulkExecute` stores a copy (captured/mutated source has no effect)
- [ ] Public `Options` getter returns independent copy
- [ ] Invalid `UseBulkExecute` throws
- [ ] No-arg uses DI snapshot; explicit object ignores DI
- [ ] Explicit object mutated mid-`await` does not affect the run; `Clone()` independence
- [ ] Two context types isolated; double `UseBulkExecute` = last-wins
- [ ] Deleted hook tests replaced: chunk counts asserted via `Harness.Generate` / chunk plans or
  `FakeAdo` executed commands (no logger to assert against until the §5.8 follow-up)

Integration (Sqlite first, then SqlServer/Npgsql):

- [ ] DI `ThrowIfZeroAffected=true` → zero-match throws with correct `OperationIndex`
- [ ] Explicit object without flag suppresses for that call only
- [ ] Chunk splitting still verified via `FakeAdo` executed-command counts (hook-based assertions removed)
- [ ] DI `CommandTimeout` flows to command

Samples:

- [ ] Per-context sample: `UseBulkExecute` in Host setup + plain calls + one explicit-object override

---

## 9. Docs & samples to update

- [ ] `README.md` — "Per-DbContext options" section (zero-config note + override table)
- [ ] `NUGET_README.md` — mirror section
- [ ] `docs/DESIGN.md` § "Options Bag" — per-context + precedence
- [ ] Samples — per-context setup in Host + override example
- [ ] XML docs on `BulkExecuteOptions`, `UseBulkExecute`, existing overloads

---

## 10. Rollout

1. Implement `Clone/CopyTo/Validate` + `OnCommandText` deletion + DI extension + `UseBulkExecute` + `ResolveEffective` + unit tests.
2. Sqlite integration tests, then SqlServer/Npgsql equivalents.
3. Docs/samples (including `OnCommandText` removal notice).
4. Full suite + `Samples.Sqlite` smoke.
5. Follow-up (separate change): `ILogger` support per the §5.8 sketch.

---

## 11. Decisions (locked)

1. DI-only: `UseBulkExecute` per `DbContext` type. No static, no `AddBulkExtensions`, no patch overloads.
2. Method name: `UseBulkExecute` (extension class `BulkExecuteOptionsExtension`, builder file `BulkExecuteBuilderExtensions`) — distinctive vs the generic `UseBulkExtensions`; matches the library's `BulkExecute*` vocabulary.
3. Object overload = full replacement (DI ignored), not merge.
4. Explicit-object clone-on-entry stays (correctness over unmeasurable saving).
5. `OnCommandText` deleted now (no-op `[Obsolete]` bridge for one minor only if already 1.x, else outright); `ILogger` follow-up later per §5.8.
6. Doc lives at `docs/PLAN-global-bulk-options.md` (consider renaming to `PLAN-per-dbcontext-options.md` on implementation).

---

## 12. Why this preserves zero-config

- No `UseBulkExecute` → no extension → resolver returns `new BulkExecuteOptions()`: identical to today.
- No service registration, builder call, or new package required.
- Existing overloads unchanged; only additions are `Clone/CopyTo/Validate`, the extension, and `UseBulkExecute`.

---

