# Release Approval Gate and README Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gate NuGet publishing behind an approvable GitHub Environment and correct both README files.

**Architecture:** Reuse the existing `Production` environment (its `main` branch / `v*` tag policies already match release tags). Add `environment: Production` to the single `publish` job in `release.yml`, add `niravinfo` as a required reviewer via the GitHub REST API, and bring `README.md` and `NUGET_README.md` into sync.

**Tech Stack:** GitHub Actions YAML, GitHub REST API (`gh` CLI), Markdown.

**Spec:** The approved design in the conversation dated 2026-11-21. No separate spec file.

## Global Constraints

- Work on a new branch off `main`; do not create a worktree.
- Environment name is exactly `Production` (case as shown by the API).
- Required reviewer is exactly `niravinfo` (user id `14833460`).
- `prevent_self_review` must remain `false` — a single reviewer approving their own release is the intended flow.
- Deployment branch policy must remain `protected_branches: false`, `custom_branch_policies: true` with rules `main` (branch) and `v*` (tag).
- Both `README.md` and `NUGET_README.md` receive the same three fixes.
- The core package line `dotnet add package NSLabs.EFCore.Extensions` is removed from all docs; provider packages carry it transitively via `ProjectReference`.
- No source code changes.

## Review Focus

- **NuGet OIDC policy mismatch:** adding an environment changes the OIDC token; the nuget.org Trusted Publishing policy's Environment field may need to be `Production`, or `NuGet/login@v1` fails on the first gated release. Pinned by Task 3's manual-step handoff.
- **Self-review deadlock:** if `prevent_self_review` were `true`, the only reviewer could not approve their own release. Pinned by Task 3's GET verification.
- **Deployment policy loss:** the `PUT` that adds reviewers replaces protection rules; if `deployment_branch_policy` is omitted, `v*` tag releases would be blocked. Pinned by Task 3's GET verification of branch policies.
- **Wrong badge targets:** badge URLs must use the real workflow filenames (`build.yml`, `release.yml`) and real NuGet package id (`NSLabs.EFCore.Extensions`). Pinned by Task 2's URL verification.
- **Incorrect transitive-install claim:** verified from the provider `.csproj` `ProjectReference` entries before editing; pinned by Task 2's grep check.

---

### Task 1: Gate the `publish` job behind `Production`

**Files:**
- Modify: `.github/workflows/release.yml:28-32`

**Interfaces:**
- Consumes: existing `Production` environment with `main` + `v*` deployment rules.
- Produces: every `publish` job run (both `release` and `workflow_dispatch` triggers) pauses for environment approval before any step executes.

- [ ] **Step 1: Create the branch**

```bash
git switch -c chore/release-gate-and-badges
```

Expected: on branch `chore/release-gate-and-badges`, clean tree.

- [ ] **Step 2: Add `environment: Production` to the `publish` job**

Insert under `jobs.publish.name` in `.github/workflows/release.yml`, with a short comment that this is the approval gate configured in Settings → Environments and that the NuGet trusted-publishing policy must match.

- [ ] **Step 3: Verify the YAML parses and the key is on the right job**

Run a YAML parse of `.github/workflows/release.yml` and grep for `environment:`. Expected: exactly one match, indented under `jobs.publish`, not under `permissions` or a step.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci: require Production environment approval for releases"
```

### Task 2: Sync both README files

**Files:**
- Modify: `README.md:1-25,84-91`
- Modify: `NUGET_README.md:1-45,83-90`

**Interfaces:**
- Consumes: workflow names from `.github/workflows/{build,release}.yml` and NuGet id `NSLabs.EFCore.Extensions`.
- Produces: both READMEs show the same badge row, install only provider packages, and have a valid deferred-builder example.

- [ ] **Step 1: Add the badge row directly under the H1 in both files**

Badges, in order: Build (`build.yml` workflow badge linking to that workflow), Release (`release.yml` workflow badge linking to that workflow), NuGet version, NuGet downloads, License MIT, .NET 10. Use `https://img.shields.io/...` for shields and the `actions/workflows/<file>/badge.svg` pattern for workflow badges. Both files get the identical row.

- [ ] **Step 2: Remove the redundant core-package install line from both files**

Delete all four occurrences: the one in `README.md`'s combined install block and the three in `NUGET_README.md` (SqlServer, Sqlite, Npgsql sections). In `README.md`, add a one-line note that each provider package brings in the core package automatically, so only one provider install is needed.

- [ ] **Step 3: Remove `ApplyRules(batch);` from the deferred-builder example in both files**

Leave `IBulkBatch batch = db.CreateBulkBatch();`, the `batch.Update<...>(...)` call, and `var r = await batch.ExecuteAsync(ct);` intact.

- [ ] **Step 4: Verify**

Grep both files. Expected: zero matches for `dotnet add package NSLabs.EFCore.Extensions` (word-boundary so provider lines remain), zero matches for `ApplyRules`, six badge links per file, and provider install lines still present.

- [ ] **Step 5: Commit**

```bash
git add README.md NUGET_README.md
git commit -m "docs: add badges and fix install and deferred-builder examples"
```

### Task 3: Configure the `Production` environment and hand off the NuGet step

**Files:** none (GitHub-side configuration via `gh api`; plus this plan document if kept in the branch).

**Interfaces:**
- Consumes: user id `14833460` for `niravinfo`.
- Produces: required reviewer configured on `Production`; a clear manual action item for the user.

- [ ] **Step 1: Add the required reviewer while preserving the deployment policy**

```bash
gh api -X PUT repos/niravinfo/NSLabs.EFCore.Extensions/environments/Production \
  -f 'prevent_self_review=false' \
  -f 'reviewers[0][type]=User' \
  -f 'reviewers[0][id]=14833460' \
  -f 'deployment_branch_policy[protected_branches]=false' \
  -f 'deployment_branch_policy[custom_branch_policies]=true'
```

Expected: HTTP 200 with the environment JSON.

- [ ] **Step 2: Verify the resulting configuration**

`gh api repos/niravinfo/NSLabs.EFCore.Extensions/environments/Production` and the `deployment-branch-policies` endpoint. Expected: one `required_reviewers` rule whose reviewer login is `niravinfo`, `prevent_self_review: false`, `custom_branch_policies: true`, and branch policies still listing `main` (branch) and `v*` (tag).

- [ ] **Step 3: Report the manual nuget.org step to the user**

State that the Trusted Publishing policy's Environment field must be set to `Production` (or confirmed blank-still-matching) before the first gated release, and that admin bypass remains enabled because the REST endpoint does not expose that toggle.

- [ ] **Step 4: Commit the plan document if it is to travel with the branch**

```bash
git add docs/superpowers/plans/2026-11-21-release-approval-and-readme-fixes.md
git commit -m "docs: add implementation plan for release approval gate"
```
