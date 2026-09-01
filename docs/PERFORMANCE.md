# Performance — Next-Level Plan (Safety-First)

> **Invariant:** This library builds and executes DML against critical data. No optimization may weaken parameterization, ordering, quoting, transaction semantics, value-converter correctness, or store-generated guards. Performance is secondary to correctness; every change must be provably behavior-preserving and verified by golden-SQL + live integration gates.

Status: proposal — no code change yet. Baseline is `net10.0` + `Microsoft.EntityFrameworkCore.Relational 10.0.0` / `SqlServer 10.0.0` (`Directory.Build.props:7`, `Directory.Packages.props:9`). `MaxParametersPerCommand = 2000` (`src/NSLabs.EFCore.Extensions/BulkExecuteOptions.cs:5`), default chunking via `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs:13`.

---

## 1. Performance model — where time actually goes

```
Build phase (CPU, in-process)          →    Execute phase (IO, out-of-process)
Fluent builders → ModelBinder →            OpenConnection → ExecuteReader per chunk
LinqPredicateTranslator /                →   SQL Server parse/plan
SetExpressionTranslator →                →   Row-store writes + HOLDLOCK
BoundOperation → Generate (chunking,    →   @@ROWCOUNT capture → SELECT @rc
StringBuilder + ParameterEmitter)
```

| Phase | Dominant cost | Order of magnitude | Notes |
|---|---|---|---|
| **DB round-trips** | Network + log fsync + lock | 10–100× | Already collapsed: `N` heterogeneous `UPDATE/UPSERT/DELETE` → 1 `DbCommand` per chunk (`docs/DESIGN.md:237`). Biggest win already shipped. |
| **Chunk count** | `MaxParametersPerCommand` splits | 2–5× | Fewer chunks → fewer reader round-trips (`src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerExecutor.cs:86`). |
| **Translation + binding** | Expression compile + reflection | 5–30% at scale | Only visible at `>500 ops` or `>2k upsert rows`. See §3. |
| **SQL string + params** | `StringBuilder`, `SqlParam` alloc | 1–5% | Allocations pressure GC; not DB correctness. |

**Rule:** optimize DB IO first (fewer/smaller chunks), then translation overhead, then allocations. Never trade safety for a 1% CPU win.

---

## 2. Non-negotiable safety invariants — every optimization must preserve

Any proposal violating these is rejected regardless of speed.

| # | Invariant | Source | What breaks if violated |
|---|---|---|---|
| S1 | **All values are parameters, never interpolated** | `DESIGN.md:241`, `SqlServerSqlGenerator.cs:359` `ParameterEmitter.EmitValue` | SQL injection, plan cache poisoning |
| S2 | **Submission order = execution order, sequential semantics** | `BulkBatch.cs:14`, `DESIGN.md:29` `GlobalIndex` | FK violations, lost writes (later op must see earlier op's row) |
| S3 | **Ambient transaction piggyback only** | `SqlServerExecutor.cs:19`, `TRANSACTIONS.md:18` | Implicit commit/rollback hides data loss; retry strategy must not wrap when `CurrentTransaction != null` |
| S4 | **Value converters + enum underlying applied** | `ModelBinder.cs:45`, `LinqPredicateTranslator.cs:64` | Wrong rows matched / corrupt writes |
| S5 | **Store-generated columns rejected** | `ModelBinder.cs:74` `EnsureWritable` | Identity/computed overwrite, runtime error |
| S6 | **Quoting via `Quote()`** | `SqlServerSqlGenerator.cs:354` `Replace("]", "]]")` | Broken identifiers, injection via table/column names |
| S7 | **Discriminator injection for TPH** | `ModelBinder.cs:83` `AddDiscriminatorPart` | Cross-hierarchy corruption |
| S8 | **Param-budget chunking hard limit** | `SqlServerSqlGenerator.cs:31` `cost > maxParametersPerCommand` | `sp_executesql` 2100-param failure, silent truncation |
| S9 | **Upsert duplicate-key pre-validation** | `BulkBatch.cs:91` `ValidateUniqueUpsertKeys` | `MERGE` error `cannot affect same row twice` at runtime |
| S10 | **`HOLDLOCK` on MERGE** | `SqlServerSqlGenerator.cs:218` `WITH (HOLDLOCK)` | Race + duplicate-key on concurrent upsert |
| S11 | **Per-op `@@ROWCOUNT` capture + `SELECT @rc` contract** | `SqlServerSqlGenerator.cs:130`, `SqlServerExecutor.cs:129` | `BulkExecuteResult.Operations[i].RowsAffected` off-by-N, `ThrowIfZeroAffected` false positive |

Add a one-line `// SAFETY S{1..11}` comment at each enforcement site before optimizing nearby code.

---

## 3. Measured hot paths — what is actually slow (audit of current code)

All references verified against current source; no speculation.

| Hot path | File | Symptom | Scale threshold |
|---|---|---|---|
| `PropertyInfo.GetValue` per cell | `BulkBatch.cs:309` `ReadMemberValue` via `ModelBinder.cs:99`, `BindUpsert:324`, `BindEntityRows:372` | Reflection invoke `Rows.Count * InsertColumns.Count` times | `>1k` entity-style rows → ms → tens of ms |
| `Expression.Lambda(...).Compile().DynamicInvoke()` fallback | `LinqPredicateTranslator.cs:371`, `SetExpressionTranslator.cs:337` `Evaluate()` | JIT + `DynamicMethod` per distinct `Contains`/`IN`/`IsNullOrEmpty` predicate | `>500` ops with captured collections |
| `GetValueConverter()` + `GetRelationalTypeMapping()` per comparison | `ModelBinder.cs:52`, called from `LinqPredicateTranslator.cs:64,81,98` and `SetExpressionTranslator.cs:232` | Metadata lookup per `WHERE` param | Hundreds of ops |
| `StringBuilder` + `string.Join` + `Distinct()` + `Quote` `Replace` per column | `SqlServerSqlGenerator.cs:127` `BuildChunk`, `155` `string.Join`, `167` `Distinct`, `354` `Quote` | Allocations + LOH for large batches | `>2k` params |
| `command.CreateParameter()` + `$"@p{Counter++}"` string alloc | `SqlServerExecutor.cs:118`, `SqlServerSqlGenerator.cs:392` | `N params` string allocs + `DbParameter` per param | `>2k` params |
| `UpsertKey` class + `SequenceEqual` | `BulkBatch.cs:122` | Hash + linear scan per upsert key validation | `>5k` upsert rows |
| `ParameterReplacer` visitor alloc per `match` row | `BulkBatch.cs:363`, `Internal/ParameterReplacer.cs:5` | `ExpressionVisitor` per entity row | Entity-style `Update(rows, match)` |

DB still dominates, but these are the only safe CPU targets.

---

## 4. Optimization tiers — safe-first classification

### Tier 0 — Zero-risk (behavior-identity, pure caching/presizing)

No SQL or semantics change; only cache or pre-size. Safest to ship first. Each must keep `S1..S11`.

| Opt | Change | Safety argument | Verification |
|---|---|---|---|
| T0-1 | **Cache property getters** — `ConcurrentDictionary<IProperty, Func<object, object?>>` compiled via `Expression.Property` once vs `PropertyInfo.GetValue` per row | Same `ConvertToProvider` still applied after read; same `EnsureWritable` guard | Golden-SQL unchanged; integration row values byte-identical |
| T0-2 | **Cache value converter per `IProperty`** — `ConcurrentDictionary<IProperty, IValueConverter?>` at first `ConvertToProvider` | Same converter instance already returned by EF metadata; lookup was redundant | Predicate `Where(x=>x.Status==Pending)` with enum/value-converter integration test still passes |
| T0-3 | **Presize containers** — `List<SqlParam>(capacity: estimatedParamCount)`, `List<PendingUnit>(ops.Count)`, `Dictionary<int,int>(ops.Count)`, `StringBuilder(capacity: estChars)` | Presize does not affect enumeration order `S2` | No observable change; `ChunkingTests.cs:4` still green |
| T0-4 | **Cache compiled `Evaluate` delegates** — `ConcurrentDictionary<Expression, Func<object?>>` for the `TryCompile` fallback path in `LinqPredicateTranslator.cs:371` / `SetExpressionTranslator.cs:341` | Compiled delegate returns same `Evaluate(node)` result; captured closure values re-evaluated via same closure snapshot | `PredicateLikeTests.cs` + `ComputedSetGoldenSqlTests.cs` unchanged |
| T0-5 | **Pool `StringBuilder` per `BuildChunk`** — `ZString` / `StringBuilderPool` or `ArrayPool<char>` rented string builder, returned in `finally` | Pool is internal to generator; emitted `CommandText` string is still `ToString()` snapshot | Golden-SQL byte-identical |

Expected gain: 10–35% faster build phase at `1k+` ops, zero risk to DB correctness when verified.

### Tier 1 — Low-risk (small logic tightening, needs golden-SQL lock)

SQL text may change only in whitespace/ordering that is still semantically identical; lock with snapshot.

| Opt | Change | Safety argument | Verification |
|---|---|---|---|
| T1-1 | **Eliminate LINQ alloc in hot loop** — replace `OperationIndicesOf(...).Distinct()` + `Select` + `string.Join` with manual `HashSet<int>` + loop in `BuildChunk` | Order preserved; `HashSet` only for distinct, output order is still insertion order (`GlobalIndex` ascending) | `ChunkingTests.Parameter_budget_splits_operations...` golden asserts |
| T1-2 | **Make `UpsertKey` a `readonly record struct` with `HashCode.Combine` + custom `EqualityComparer<UpsertKey>`** vs class + `SequenceEqual` | Same equality semantics, avoids per-key `List<object?>` + `SequenceEqual` alloc | `ValidateUniqueUpsertKeys` throws on same duplicate pair with same message |
| T1-3 | **Inline `Quote` hot loop** — avoid `string.Replace` alloc when no `]` in identifier (fast path `IndexOf(']')==-1`) | `Quote` still escapes correctly; fast path is identity | All golden-SQL still `Quote` brackets correctly |
| T1-4 | **Reuse `ParameterReplacer` instance via pooled visitor or manual iterative replace** | Same replacement semantics | `MixedAndEntityRowTests.cs` custom `match: (row,x)=>x.Code==row.Code` still correct |

Each requires before/after `Harness.Generate` diff reviewed.

### Tier 2 — Medium (provider/math or execution change, needs integration proof)

| Opt | Change | Safety argument | Risk note |
|---|---|---|---|
| T2-1 | **Lightweight `DbParameter` creation** — reuse `CreateParameter` but set `DbType`/`Size` from `IProperty.GetRelationalTypeMapping()` to improve plan reuse | Still parameterized `S1`; plan cache benefit | Must not set wrong `DbType` for value converters — test with `decimal`, `Guid`, `string` + converters |
| T2-2 | **Chunk-size heuristics** — emit single `DECLARE` block not interleaved; no change to chunk boundaries | Still respects `MaxParametersPerCommand` `S8` | Verify `ExecutorCoreTests.cs` fake ADO reader still finds `FieldCount`/`NextResult`/`GetInt32(k)` contract `S11` |

### Tier 3 — Explicitly forbidden (correctness over speed)

| Forbidden | Why |
|---|---|
| `SetRaw("Price=Price*1.1")` or string-interpolated SET | Breaks `S1` + `S6`; rejected in `COMPUTED_SET_SUPPORT.md:60` |
| Combining ops into `UPDATE ... CASE WHEN` | Breaks `S2` sequential semantics (`DESIGN.md:31`) |
| Parallel chunk execution without ambient `SERIALIZABLE` | Breaks `S2` + `S10`; later ops see stale state |
| Removing `HOLDLOCK`, discriminator, or `EnsureWritable` | Breaks `S10`,`S7`,`S5` |
| Skipping `ValidateUniqueUpsertKeys` for speed | Breaks `S9`; defer to raw `MERGE` error loses op-index diagnostics |
| Auto-wrapping in a transaction to "speed up" | Breaks `S3` transactional contract (`TRANSACTIONS.md:4`) |

---

## 5. Recommended implementation order — safe rollout

Every phase: **measure → implement → prove**.

```
Phase 0 — Harness (1 day, no prod change)
  Add BenchmarkDotNet project: benchmarks/NSLabs.EFCore.Extensions.Benchmarks/
  Scenarios: 10 ops, 100 ops, 1000 ops; upsert 100/1000 rows; with/without converters; IN (100 values)
  Baseline numbers committed to this doc; CI not gating on numbers yet

Phase 1 — Tier 0 caches (2-3 days)
  T0-1 (property getters) → T0-2 (converter cache) → T0-4 (Evaluate delegate cache) → T0-3/5 (presize/pool)
  After each: dotnet test Tests.Unit (golden-SQL) + Tests.Integration (Testcontainers) must be 0 failed
  Commit each opt atomically with Safety comment

Phase 2 — Tier 1 alloc tightening (2 days)
  T1-1 → T1-2 → T1-3 → T1-4
  Review SQL diffs; no semantic change approved without updated golden approval

Phase 3 — Tier 2 if profiling still shows pressure (opt-in)
  Only after Phase 1+2 numbers show executor not DB-bound
  T2-1 with `DbType` mapping under feature flag; A/B with integration matrix
```

Roll forward only if both suites pass + benchmark shows no regression.

---

## 6. How to make it still correct — verification gates

For **every** optimization PR, all of these must be true before merge:

- [ ] `dotnet test tests/NSLabs.EFCore.Extensions.Tests.Unit -c Release` — 0 failed (85 golden-SQL incl `UpdateGoldenSqlTests.cs`, `UpsertGoldenSqlTests.cs`, `ComputedSetGoldenSqlTests.cs`, `PredicateLikeTests.cs`, `ChunkingTests.cs`, `ExecutorCoreTests.cs` via `FakeAdo.cs`)
- [ ] `dotnet test tests/NSLabs.EFCore.Extensions.Tests.Integration -c Release` — 0 failed / 0 unexpected skip (requires Docker; `SqlServerFixture.cs:8`), covers `HOLDLOCK`, `@@ROWCOUNT` per-op (`TransactionAndSemanticsTests.cs`), computed SET v1+v2 (`ComputedSetExecutionTests.cs`, `ComputedSetV2ExecutionTests.cs`)
- [ ] Golden-SQL diff reviewed: `Harness.Normalize` output byte-identical except approved whitespace
- [ ] `ValidateUniqueUpsertKeys` still throws with both `OpIndex + RowIndex` on duplicate (`ValidationTests.cs`)
- [ ] `ThrowIfZeroAffected` both with and without ambient `Database.BeginTransactionAsync()` (`TransactionTests.cs`) — rollback semantics unchanged (`TRANSACTIONS.md:92`)
- [ ] Value-converter + enum + TPH discriminator still exercised (`TestModel.cs` + mixed tests)
- [ ] No new `catch {}` swallowing; all new cache lookups use `TryGetValue`/`GetOrAdd` without double-checked locking bugs
- [ ] `OnCommandText` callback still invoked once per chunk with full `CommandText` (`SqlServerExecutor.cs:125`)
- [ ] Thread-safety preserved: `BulkBatch` remains not thread-safe by design (`DESIGN.md:110`); any new static caches are `ConcurrentDictionary` only; no shared mutable per-batch state

**Fuzz gate (recommended before Tier 1):** generate 200 random batches (random ops × random predicates × random converters) and assert `Generate` round-trips through parse without throws and `ExecuteAsync` via `FakeAdo` returns per-op counts matching chunk plan's `OperationIndices` length.

---

## 7. What to run to prove a gain

```bash
# Unit (no Docker, <1s)
dotnet test tests/NSLabs.EFCore.Extensions.Tests.Unit -c Release --logger "console;verbosity=minimal"

# Integration (Docker, ~5s after image pull)
sg docker -c "dotnet test tests/NSLabs.EFCore.Extensions.Tests.Integration -c Release"

# Benchmarks (once benchmark project exists)
dotnet run -c Release --project benchmarks/NSLabs.EFCore.Extensions.Benchmarks -- --filter "*BulkBatch*"
```

Compare `Before` vs `After` median for `Batch 1000 ops` and `Upsert 1000 rows`. Accept only if DB result correctness gates above still pass and median improves with `p < 0.05` over 10 runs; otherwise revert.

---

## 8. Multi-target note — framework choice affects perf, not just reach

`Directory.Build.props:7` is `net10.0` — correct for `EF 10.0.0` (`net10.0`-only, no `netstandard` — `learn.microsoft.com/ef/core/miscellaneous/platforms: EF 5 was last to support netstandard`). That already gives `net10.0` JIT, `FrozenDictionary`, `Span`, `Unsafe`.

If you later need `net8.0` reach, use **multi-target** `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` with per-TFM `PackageReference` (not `netstandard2.1` — `netstandard2.1` cannot consume `EF 8/10` `net8.0`/`net10.0` assets and loses runtime intrinsics). Each consumer gets the best binary; no perf penalty for `net10.0` users.

---

## 9. Future providers

Same pipeline (`Internal/LinqPredicateTranslator.cs`, `SetExpressionTranslator.cs`, `ModelBinder.cs`) is reused. SQLite plan (`docs/SQLITE_SUPPORT_PLAN.md:4`) already specifies quoting + `changes()` vs `@@ROWCOUNT` differences — any new provider must re-establish `S1..S11` with its own golden suite before tiered opts are ported.

---

## Appendix — template for each optimization PR description

```
Title: perf(T0-1): cache IProperty getters in ModelBinder/BulkBatch

Safety: S4,S5 preserved — converter still applied after cached read; EnsureWritable before write
Files: src/NSLabs.EFCore.Extensions/Internal/ModelBinder.cs:99, src/NSLabs.EFCore.Extensions/BulkBatch.cs:309
Before/After bench: Batch 1000 entity rows — Before 42.3ms, After 28.1ms (-33%)
Tests: Unit 85/85, Integration 65/65, golden diff: none
```

