# Side-by-side .NET 10 / .NET 11 support — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** build and test the library for both `net10.0` (EF 10) and `net11.0` (EF 11) from one codebase.

**Architecture:** central package management with TFM-conditional `PackageVersion` groups; shared source with minimal `#if NET11_0` shims for provider-internal API differences.

**Tech Stack:** .NET SDK 10/11, EF Core 10.0.15 / 11.0.0, Npgsql EF provider 10.0.4 / 11.0.0, xunit.v3 4.0.3.

**Spec:** `docs/superpowers/specs/2026-11-21-net10-net11-side-by-side-design.md`

## Global Constraints

- `net10.0` must stay on EF Core `_10.0.15_` and `Npgsql.EntityFrameworkCore.PostgreSQL` `_10.0.4_`.
- `net11.0` uses EF Core `_11.0.0_` and `Npgsql.EntityFrameworkCore.PostgreSQL` `_11.0.0_`.
- `Microsoft.Extensions.*` versions must match the EF major within each TFM.
- No public API changes.

## Review Focus

- `SqlServerOptionsExtension` internal API shape across EF 10/11 (`SqlServerDefaultCompatibilityLevel`, `EngineType`).
- Golden SQL snapshots that depend on SQL Server compatibility level default (150 vs 160).
- Npgsql/SQLite provider-internal APIs used by the provider packages.
- xunit.v3 MTP test discovery on a multi-targeted project.
- NuGet package layout: both `lib/net10.0` and `lib/net11.0` assets present.

---

### Task 1: Multi-target build configuration

**Files:** Modify `Directory.Build.props`, `Directory.Packages.props`.

- [ ] **Step 1:** Change `<TargetFramework>net10.0</TargetFramework>` to `<TargetFrameworks>net10.0;net11.0</TargetFrameworks>` in `Directory.Build.props`.
- [ ] **Step 2:** Restructure `Directory.Packages.props` into a shared third-party group plus `net10.0`/`net11.0` groups for the Microsoft stack.
- [ ] **Step 3:** Run `dotnet restore NSLabs.EFCore.Extensions.slnx --force`; expect success with no NU1605/NU1202 errors.
- [ ] **Step 4:** Commit.

### Task 2: Compile for both target frameworks

**Files:** Modify provider source files only where EF 11 API shapes differ.

- [ ] **Step 1:** Run `dotnet build NSLabs.EFCore.Extensions.slnx --no-restore`; collect compile errors for `net11.0`.
- [ ] **Step 2:** Add minimal `#if NET11_0` adapters for any renamed/moved provider-internal APIs; keep `net10.0` code path byte-for-byte equivalent.
- [ ] **Step 3:** Rebuild until both TFMs compile with 0 errors.
- [ ] **Step 4:** Commit.

### Task 3: Verify behavior on both target frameworks

**Files:** possibly golden SQL test fixtures.

- [ ] **Step 1:** Run each unit-test project with `--framework net10.0` and `--framework net11.0`; all tests must pass.
- [ ] **Step 2:** If EF 11 golden SQL differs only due to documented behavior changes, update fixtures with an explicit `NET11_0` expectation and a comment; do not weaken assertions.
- [ ] **Step 3:** Commit.

### Task 4: Packaging and documentation

**Files:** `NUGET_README.md`, `README.md` if needed.

- [ ] **Step 1:** Verify `dotnet pack` produces `lib/net10.0` and `lib/net11.0` assets for each packable project.
- [ ] **Step 2:** Document the two supported TFMs and their EF versions.
- [ ] **Step 3:** Commit.
