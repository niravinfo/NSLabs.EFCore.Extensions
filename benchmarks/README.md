# NSLabs.EFCore.Extensions Benchmarks

BenchmarkDotNet suite for measuring bind, SQL generation, and execution performance.
No real database is required: bind/generation run offline (fake SQL Server connection
string, same pattern as the golden-SQL tests); execution benchmarks use shared-cache
in-memory SQLite.

## Run on GitHub (preferred)

Manual-only workflow — never runs on push/PR:

**Actions → Benchmarks → Run workflow**

| Input | Meaning | Default |
|-------|---------|---------|
| `filter` | BDN `--filter`, e.g. `*EntityRowMatch*` | `*` (all) |
| `job` | `Dry` (smoke) / `Short` (CI A/B) / `Default` (thorough) | `Short` |

Results upload as the `benchmark-results-*` artifact (CSV/HTML/GitHub-md, 90-day retention).

**Comparison rules:**
- Only compare runs from this workflow (same ubuntu runner image).
- Never compare against local Windows runs.
- On shared runners, trust `Allocated` more than `Mean` (timings are noisy).

## Run locally

Always Release:

```bash
dotnet run -c Release --project benchmarks/NSLabs.EFCore.Extensions.Benchmarks
```

Filter by class or method:

```bash
dotnet run -c Release --project benchmarks/NSLabs.EFCore.Extensions.Benchmarks -- --filter '*PredicateTranslation*'
dotnet run -c Release --project benchmarks/NSLabs.EFCore.Extensions.Benchmarks -- --filter '*EntityRowMatch*BindAndGenerateCustomMatch*'
dotnet run -c Release --project benchmarks/NSLabs.EFCore.Extensions.Benchmarks -- --filter '*SqliteExecute*'
```

List available benchmarks:

```bash
dotnet run -c Release --project benchmarks/NSLabs.EFCore.Extensions.Benchmarks -- --list flat
```

Pass-through BenchmarkDotNet options (e.g. `--memory`, exporters, jobs) work after `--`.

## Scenarios

| Class | Measures | Targets |
|-------|----------|---------|
| `Bind.PredicateTranslationBenchmarks` | Predicate translation per operation count | P2 |
| `Bind.SetExpressionBenchmarks` | Computed SET translation (uncached `Evaluate`) | P2 |
| `Generation.SqlGenerationBenchmarks` | Chunk-plan generation (intermediate strings) | P4 |
| `Generation.EntityRowMatchBenchmarks` | PK-match vs custom-match entity-row bind+generate | P3 |
| `Generation.LargeInListBenchmarks` | Large `Contains` fast-path emit | — |
| `Execution.SqliteExecuteBenchmarks` | End-to-end execute, in-memory SQLite | baseline |

`[MemoryDiagnoser]` is enabled on all benchmarks (time + allocations).

## Baseline workflow

1. Capture a baseline before an optimization (run the GitHub workflow with `job=Short`, or locally with the same filter).
2. Apply the change.
3. Re-run the **same** filter/job and compare Mean / Allocated columns.

CI (`build.yml`) only compile-checks this project; the `benchmarks.yml` workflow is the only place it executes.
