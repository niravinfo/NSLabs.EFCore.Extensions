# ExpressionEvaluatorCache Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `feature/PerformanceImprovements` safe and simple enough to merge into `main` by adding parity tests, removing the risky hand-written operator interpreter, pinning conversion semantics, and correcting unsupported benchmark claims in the docs.

**Architecture:** Keep the two high-value caches (`MemberInfo` getters and `Type` default comparers) plus the `ConditionalWeakTable` compile fallback. Remove the binary/unary/conditional interpreter and its two shape caches so rare shapes use compiled delegates — the same mechanism the old code used, so behavior is preserved by construction. Add tests at the evaluator level and golden-SQL level.

**Tech Stack:** C# / .NET 10, xUnit, EF Core 10, BenchmarkDotNet (not run in this work).

**Spec:** The three code-review reports delivered 2026-11-21 (PerformanceImprovements review, P4 review, branch-topology review) and `docs/IMPROVEMENT_PLAN.md` P2 section.

## Global Constraints

- No benchmarks or BenchmarkDotNet tests are run or added.
- No changes to `main`, remote branches, or the current checkout; all work happens on worktree branch `fix/perf-review` based on `502ba4f`.
- Existing tests, golden SQL, and parameter values/types must remain unchanged except for the explicitly documented captured-conversion behavior.
- Code must stay clear and simple: prefer deleting clever code over adding new clever code.

## Review Focus

- **Coalesce with conversion** — a captured `value ?? fallback` where types differ must produce the same result and type as compiled C#; pinned by `Evaluate_coalesce_with_conversion_matches_compiled_runtime`.
- **Per-row dynamic values** — the same cached getter must read different instances without caching values; pinned by `Evaluate_uses_current_value_for_each_instance`.
- **Captured numeric conversions** — the new evaluator performs `Convert`/`ConvertChecked` rather than stripping them; this is intentional (more correct ADO.NET parameter types). Pinned by golden-SQL parameter-type assertions in `ComputedSetGoldenSqlTests` and a predicate test in `UpdateGoldenSqlTests`.
- **Unusual member shapes** — static members, nested member chains, and arrays/lists still work through the structural paths; pinned by evaluator unit tests.
- **Docs accuracy** — status claims must reference only checked-in artifacts; pinned by manual doc diff review (prose earns no test).

---

### Task 1: Evaluator parity tests

**Files:**
- Create: `tests/NSLabs.EFCore.Extensions.Tests.Unit.SqlServer/ExpressionEvaluatorCacheTests.cs`
- Modify: `tests/NSLabs.EFCore.Extensions.Tests.Unit.SqlServer/ComputedSetGoldenSqlTests.cs`
- Modify: `tests/NSLabs.EFCore.Extensions.Tests.Unit.SqlServer/UpdateGoldenSqlTests.cs`

**Interfaces:**
- Consumes: `NSLabs.EFCore.Extensions.Internal.ExpressionEvaluatorCache.Evaluate(Expression)` and `.GetDefaultEqualityComparer(Type)`.
- Consumes: `Harness.GenerateSingle(...)` and `Harness.Params(...)` (unit SqlServer harness).
- Produces: regression tests that remain valid after Task 2/3.

- [ ] **Step 1: Write failing parser-level tests** in the new file:

```csharp
[Fact] public void Evaluate_coalesce_with_conversion_matches_compiled_runtime()
// Build: Expression.Coalesce(Expression.Constant(3, typeof(int?)),
//                            Expression.Constant(4L), conversion: lambda int->long)
// Assert: result type long and value 3L, identical to lambda.Compile().Invoke()
[Fact] public void Evaluate_uses_current_value_for_each_instance()
// Two holders sharing one PropertyInfo; assert Evaluate(memberExpr) returns each own value
[Fact] public void Evaluate_reads_nested_member_chain()
// holder.Child.Value via MemberExpression chain; assert expected literal
[Fact] public void Evaluate_handles_new_array_and_list_initializer()
// assert array contents and list contents
[Fact] public void Evaluate_falls_back_to_compile_for_unsupported_node_shapes()
// e.g. MethodCallExpression on a captured object; assert result equals compiled lambda result
```

- [ ] **Step 2: Run the new test class only** (`dotnet test tests/...Unit.SqlServer/... --filter FullyQualifiedName~ExpressionEvaluatorCacheTests`) and confirm the coalesce test fails with the wrong type/value while the others pass (characterization tests are expected to pass immediately).

- [ ] **Step 3: Add golden-SQL parameter-type tests** (characterization, no production change needed):

```csharp
// ComputedSetGoldenSqlTests: Set(x => x.Amount, x => x.Amount + (decimal)(int)capturedDouble)
// Assert parameter @p0 is decimal with expected value.
// UpdateGoldenSqlTests: Update<AuditLog>(...Where(x => x.Id > (long)capturedInt))
// Assert parameter @p1 is long with expected value.
```

- [ ] **Step 4: Run the full SqlServer unit test project** and confirm all tests pass.

- [ ] **Step 5: Commit tests**

```bash
git add tests/ && git commit -m "test: pin expression evaluator parity and captured conversion types"
```

### Task 2: Simplify the evaluator

**Files:**
- Modify: `src/NSLabs.EFCore.Extensions/Internal/ExpressionEvaluatorCache.cs`

**Interfaces:**
- Consumes: tests from Task 1.
- Produces: unchanged public surface of `Evaluate` and `GetDefaultEqualityComparer`.

- [ ] **Step 1: Delete the operator interpreter**

Remove the `BinaryOpCache`, `UnaryOpCache`, `BinaryOpKey`, `UnaryOpKey`, `EvaluateBinary`, `ApplyBinary`, `CompileBinaryOp`, `EvaluateUnary`, and `CompileUnaryOp` members and the `BinaryExpression`, `UnaryExpression`, and `ConditionalExpression` cases in `Evaluate`. Those shapes fall through to `CompileAndEvaluate`, which is the pre-P2 behavior and uses the weak-keyed compile cache.

- [ ] **Step 2: Restore old comparer semantics**

Change `GetDefaultEqualityComparer` to return the result of `GetProperty("Default")?.GetValue(null)` without throwing when the property is missing, matching pre-P2 `LinqPredicateTranslator`.

- [ ] **Step 3: Run the evaluator tests** — all pass. Then run the full solution test suite; report any pre-existing failures by name.

- [ ] **Step 4: Commit**

```bash
git add src/NSLabs.EFCore.Extensions/Internal/ExpressionEvaluatorCache.cs
git commit -m "refactor: simplify evaluator to getter/comparer caches plus compile fallback"
```

### Task 3: Correct documentation claims

**Files:**
- Modify: `docs/IMPROVEMENT_PLAN.md` (P2 status line ~141)

- [ ] **Step 1: Rewrite the P2 status line** so it cites only `benchmarks/BASELINE.md`, states that predicate-side A/B data was not captured, removes the uncommitted `Baseline_New.md` / `New_Improvements.md` references and the EPYC 7763 claim, and records the intentional captured-conversion behavior.

- [ ] **Step 2: Verify diff** — only the P2 status line changes.

- [ ] **Step 3: Commit**

```bash
git add docs/IMPROVEMENT_PLAN.md
git commit -m "docs: correct P2 benchmark references and conversion note"
```

### Task 4: Final verification and handoff

- [ ] **Step 1: Build** `dotnet build NSLabs.EFCore.Extensions.slnx -c Release --nologo` (expect 0 errors).
- [ ] **Step 2: Test** `dotnet test NSLabs.EFCore.Extensions.slnx -c Release --nologo` (expect 0 failures; report skipped counts).
- [ ] **Step 3: Review diff** `git diff 502ba4f..HEAD --stat` and confirm no benchmark files, no branch state changes.
- [ ] **Step 4: Report** ready-to-merge status for `fix/perf-review`; do not merge or push without explicit approval.
