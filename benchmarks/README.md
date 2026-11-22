# NSLabs.EFCore.Extensions Benchmarks

BenchmarkDotNet suite for measuring bind, SQL generation, and execution performance.
No real database is required: bind/generation run offline (fake SQL Server connection
string, same pattern as the golden-SQL tests); execution benchmarks use shared-cache
in-memory SQLite.

## Run on GitHub

The workflow is manual only; it never runs on push or pull requests. In Actions, open Benchmarks and choose Run workflow.

| Input | Meaning | Default |
|-------|---------|---------|
| `baseline_ref` | Git ref to measure as the baseline | `main` |
| `filter` | BDN `--filter`, e.g. `*EntityRowMatch*` | `*` (all) |
| `job` | `Dry` (smoke) / `Short` (CI A/B) / `Default` (thorough) | `Short` |
| `framework` | Target framework for the candidate | `net11.0` |
| `baseline_framework` | Target framework for the baseline | `net10.0` |

The ref you dispatch is the candidate. Both revisions are built and measured in one
job on the same runner with the same SDK, so the comparison does not depend on which
machine GitHub happened to assign. The runner image is pinned to `ubuntu-26.04`.
This repo is public, so standard runners are free and unlimited.

Results upload as the `benchmark-ab-*` artifact (CSV/HTML/GitHub-md, 90-day retention)
with two folders: `candidate/BenchmarkDotNet.Artifacts/` for the candidate and
`baseline/BenchmarkDotNet.Artifacts/` for the baseline. Both checkouts are siblings
so BenchmarkDotNet sees only one benchmark project per run.

Comparison rules:
- Compare the two folders from the same run; never compare numbers from separate runs.
- Never compare against local Windows runs.
- On shared runners, trust `Allocated` more than `Mean` (timings are noisy).

## Run locally

Use the Release configuration:

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
| `Generation.LargeInListBenchmarks` | Large `Contains` fast-path emit | none |
| `Execution.SqliteExecuteBenchmarks` | End-to-end execute, in-memory SQLite | baseline |

`[MemoryDiagnoser]` is enabled on all benchmarks (time + allocations).

## Baseline workflow

1. Have the baseline commit available on a branch or tag on GitHub (usually `main`).
2. Dispatch the `Benchmarks` workflow on the branch with your change, setting `baseline_ref` to that baseline and using the same `filter` and `job` for both sides.
3. Download the `benchmark-ab-*` artifact and compare the Mean and Allocated columns in `candidate/` against `baseline/`. Both were measured in the same job on the same runner.

CI (`build.yml`) only compile-checks this project; the `benchmarks.yml` workflow is the only place it executes.
