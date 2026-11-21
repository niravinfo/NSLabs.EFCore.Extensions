# Same-runner benchmark A/B design

## Intent

Benchmark comparisons today happen across two separate Actions runs. Those runs
land on different physical hosts and different image versions: the pre-optimization
baseline used Ubuntu 24.04 / EPYC 9V45 @ 4.35 GHz / .NET 10.0.12, the next run used
Ubuntu 26.04 / EPYC 9V74 @ 3.69 GHz / .NET 10.0.15. Mean timings moved up to 3x
while Allocations were unchanged. That is machine drift, not code.

The goal is an A/B that runs both revisions sequentially inside **one** workflow job,
so they share one CPU, one image, one SDK and one set of neighbours. Free, no owned
VM or VPS.

## Goals / non-goals / anti-goals

- **Goal:** one `Benchmarks` run produces results for a baseline ref and the candidate ref, measured on the same runner.
- **Goal:** results stay uploadable as a single artifact with both sides clearly separated.
- **Non-goal:** pinning a physical machine. GitHub-hosted runners remain random across runs; this design makes each comparison internally consistent instead.
- **Anti-goal:** a new VM, paid runners, or a comparison engine that reimplements BenchmarkDotNet.

## Approaches considered

- **A — two separate workflow runs (status quo).** Simple, but each side gets a different machine. Rejected: this is the problem.
- **B — one job, two checkouts, two sequential BDN runs (chosen).** Same hardware and toolchain for both sides. Costs roughly twice the Actions minutes and a little workflow complexity. No infrastructure.
- **C — self-hosted runner on a free Oracle Cloud VM.** Gives a fixed machine across months, but adds infrastructure to maintain. Not needed for most A/B decisions; keep as a later option.

Also pin the image label to `ubuntu-26.04` — the image `ubuntu-latest` points at today (migration completed 19 Nov 2026) — instead of the floating `ubuntu-latest` label. Same image as today, locked against the next move. The repository is public, so standard runners are free and unlimited; pinning the label changes nothing about billing.

## Design

Modify `.github/workflows/benchmarks.yml`:

1. Add a `baseline_ref` workflow_dispatch input, default `main`. Keep `filter` and `job` as they are; the dispatched ref remains the candidate.
2. Check out the candidate with the normal `actions/checkout` step, then check out `baseline_ref` into a second directory (`baseline/`) with another `actions/checkout` step.
3. Run `dotnet restore` and a Release `dotnet build` for each tree separately, with each command's working directory set to that tree. Keep `--no-build` semantics for the BDN run.
4. Run the benchmark command twice, sequentially, with each tree as its own working directory so BDN writes `BenchmarkDotNet.Artifacts/` under `candidate/` and `baseline/` independently. Preserve the current env-var approach for `filter` and `job` to avoid shell injection.
5. Upload a single artifact containing both result folders under `baseline/` and `candidate/` prefixes. Do not attempt automatic numeric comparison in this change — a human reading two BDN reports side by side is reliable and keeps the workflow small.
6. Keep the existing `concurrency` group so benchmark jobs never overlap.
7. Set `runs-on: ubuntu-26.04` (the current `ubuntu-latest` image, pinned so a future migration cannot change benchmark results underneath us).

## Validation

- Run the workflow via `gh workflow run` with default inputs; confirm success and that the artifact contains both `baseline/` and `candidate/` report sets.
- Confirm the two runs happened in the same job (one `Run benchmarks` step sequence in one job log).
- Confirm candidate results for a known path match the numbers from local A/B directionally, and that Allocations match exactly.
- Update `benchmarks/README.md` with the new input and how to read the artifact.

## Left to the builder

Exact step names, paths and input descriptions are covered by the implementation plan produced after this design is approved.
