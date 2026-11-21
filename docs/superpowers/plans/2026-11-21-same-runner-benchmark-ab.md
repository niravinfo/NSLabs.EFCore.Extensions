# Same-runner benchmark A/B Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make one `Benchmarks` workflow run benchmark a baseline ref and the dispatched ref sequentially on the same runner, so A/B results share one machine and toolchain.

**Architecture:** Add a `baseline_ref` dispatch input. Check out the candidate at the workspace root and the baseline into `baseline/`, install .NET once, restore/build each tree separately, then run the existing BDN command twice with each tree as its own working directory. Upload both `BenchmarkDotNet.Artifacts/` folders under prefixed paths in one artifact. Pin `runs-on: ubuntu-26.04`, which is the image `ubuntu-latest` resolves to today.

**Tech Stack:** GitHub Actions YAML, `actions/checkout@v7`, `actions/setup-dotnet@v6`, `actions/upload-artifact@v7`, BenchmarkDotNet 0.15.8, `gh` CLI.

**Spec:** `docs/superpowers/specs/2026-11-21-same-runner-benchmark-ab-design.md`

## Global Constraints

- Repository is public: standard runners are free and unlimited; pinning the image label changes nothing about billing.
- Keep `filter` and `job` inputs and the env-var pass-through to the BDN command; never interpolate the filter into shell text.
- Keep the existing `concurrency` group (`group: benchmarks`, `cancel-in-progress: false`).
- Do not add automatic numeric comparison — two BDN reports side by side are enough.
- `runs-on: ubuntu-26.04` (current `ubuntu-latest` default, pinned).
- Job timeout: 90 minutes (two sequential full suites).

## Review Focus

- Candidate tree must remain at the workspace root after the second checkout; expected candidate results still match known numbers. Test: Task 3 smoke run + artifact inspection.
- Invalid or missing baseline ref must fail at checkout, not silently benchmark the candidate twice. Test: Task 3 inspect the workflow steps and run log.
- Both result folders must appear in one artifact under distinct prefixes. Test: Task 3 artifact listing contains `BenchmarkDotNet.Artifacts/` and `baseline/BenchmarkDotNet.Artifacts/`.
- Both runs must occur in one job on one runner. Test: Task 3 single job log shows two sequential benchmark steps.
- Filter must stay in an env var to avoid shell injection. Test: Task 3 read the two run steps in the final YAML.

---

### Task 1: Rework the workflow for a same-job A/B

**Files:**
- Modify: `.github/workflows/benchmarks.yml`

**Interfaces:**
- Consumes: the existing dispatch inputs `filter` and `job`; `actions/checkout@v7`, `actions/setup-dotnet@v6`, `actions/upload-artifact@v7`.
- Produces: dispatch input `baseline_ref` (string, default `main`); artifact `benchmark-ab-${{ github.run_id }}` containing candidate results at `BenchmarkDotNet.Artifacts/` and baseline results at `baseline/BenchmarkDotNet.Artifacts/`.

- [ ] **Step 1: Add the input and change the job's runner and timeout**

In `on.workflow_dispatch.inputs`, add `baseline_ref` with description `"Git ref to benchmark as the baseline"`, `required: false`, `default: "main"`. Change the job to `runs-on: ubuntu-26.04` and `timeout-minutes: 90`. Update the top-of-file comment to say the baseline and candidate run in one job on one runner.

- [ ] **Step 2: Check out both revisions**

Keep the existing `Checkout` step as the candidate. Add a second step named `Checkout baseline` using `actions/checkout@v7` with `ref: ${{ inputs.baseline_ref }}` and `path: baseline`. Do not add `clean` or `fetch-depth` options.

- [ ] **Step 3: Install .NET once**

Leave the existing `Setup .NET` step unchanged, using `dotnet-version: 10.0.x`.

- [ ] **Step 4: Restore and build each tree**

Replace the single `Restore` and `Build (Release)` steps with four steps: `Restore (candidate)`, `Restore (baseline)`, `Build (Release, candidate)`, `Build (Release, baseline)`. Each runs the same command as today, with `working-directory` set to `.` for candidate and `baseline` for baseline. Keep `--no-restore` on both build steps.

- [ ] **Step 5: Run the benchmark twice**

Replace the single `Run benchmarks` step with `Run benchmarks (candidate)` and `Run benchmarks (baseline)`, in that order. Each uses the same `env` block (`BDN_FILTER`, `BDN_JOB`) and the same folded `run:` command as today, with `working-directory` set to `.` and `baseline` respectively.

- [ ] **Step 6: Upload one combined artifact**

Change the `Upload results` step name to `Upload A/B results`, set `name: benchmark-ab-${{ github.run_id }}`, and replace `path` with a multiline value containing `BenchmarkDotNet.Artifacts/` and `baseline/BenchmarkDotNet.Artifacts/`. Keep `if: always()`, `if-no-files-found: warn` and `retention-days: 90`.

- [ ] **Step 7: Verify the YAML parses**

Run: `gh workflow view benchmarks.yml --yaml` from the worktree (or `git diff --check` followed by a read of the whole file).
Expected: workflow shows the `baseline_ref` input and the job on `ubuntu-26.04`; no YAML errors.

- [ ] **Step 8: Commit the workflow**

```bash
git add .github/workflows/benchmarks.yml
git commit -m "ci: run baseline and candidate benchmarks on one runner"
```

---

### Task 2: Document the new input and artifact layout

**Files:**
- Modify: `benchmarks/README.md`

**Interfaces:**
- Consumes: `baseline_ref` input and the `benchmark-ab-*` artifact from Task 1.
- Produces: documentation only.

- [ ] **Step 1: Update the GitHub section**

Add `baseline_ref` to the inputs table with default `main`. Explain that the dispatched ref is the candidate, both revisions are built and measured in one job on the same runner, and the artifact contains `BenchmarkDotNet.Artifacts/` (candidate) and `baseline/BenchmarkDotNet.Artifacts/` (baseline). State that the runner image is pinned to `ubuntu-26.04` and that the repo is public so this costs no extra Actions minutes.

- [ ] **Step 2: Update the comparison rules**

Add a line: compare the two folders from the same run; do not compare numbers from separate runs. Keep the existing rules about local Windows runs and trusting `Allocated`.

- [ ] **Step 3: Commit the docs**

```bash
git add benchmarks/README.md
git commit -m "docs: describe same-runner benchmark A/B"
```

---

### Task 3: Validate with real workflow runs

**Files:** none (validation only; commit only if docs need a correction found during validation).

**Interfaces:**
- Consumes: the workflow from Task 1.
- Produces: evidence that both sides ran on one runner and the artifact has both result folders.

- [ ] **Step 1: Push the branch**

Run: `git push -u origin ci/benchmark-same-runner-ab`. This is a publish side effect; obtain human consent before running it if not already granted.

- [ ] **Step 2: Smoke-run the workflow with the Dry job**

Run: `gh workflow run benchmarks.yml --ref ci/benchmark-same-runner-ab -f filter='*Predicate*' -f job=Dry`.
Then watch the run with `gh run watch <run-id> --exit-status`.
Expected: status `success`, one job with four restore/build steps and two benchmark steps.

- [ ] **Step 3: Inspect the artifact from the smoke run**

Download with `gh run download <run-id>` and list files.
Expected: both `BenchmarkDotNet.Artifacts/results/` and `baseline/BenchmarkDotNet.Artifacts/results/` exist and contain `*Predicate*report-github.md`.

- [ ] **Step 4: Run a real Short-job A/B**

Run: `gh workflow run benchmarks.yml --ref ci/benchmark-same-runner-ab -f filter='*SetExpression*' -f job=Short`, then watch it.
Expected: status `success`; both the baseline and candidate reports exist. Since both refs currently point at the same code (the candidate branch was forked from `main`), the two sides' `Allocated` columns should agree within ~1% and their `Mean` columns should be close. That is the sanity check that the same-runner setup works.

- [ ] **Step 5: Report**

State both run IDs, confirm both sides shared one runner, show the artifact layout, and compare the two sides' numbers. If any step failed, use systematic debugging before reporting success.
