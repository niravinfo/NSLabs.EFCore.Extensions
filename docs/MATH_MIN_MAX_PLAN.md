# Plan: `Math.Min` / `Math.Max` (+ nested `Min(Round(...))`) in computed `SET` expressions

Status: **plan only — no code changes yet, pending review**.
Scope: `SET` value expressions (`Update.Set` / `SetProperty`, upsert `Update(...)`).
Non-scope: `Where` / `UpdateWhen` predicates (verified: `Internal/LinqPredicateTranslator.cs`
has zero `Math` handling today — a separate follow-up if wanted).

## 1. Verified baseline (do not re-state incorrectly)

1. Gate is `src/NSLabs.EFCore.Extensions/Internal/SetExpressionTranslator.cs:137-158`:
   `Math`/`MathF` by declaring type + name switch; only `Abs/Ceiling/Floor/Round(+Truncate)`.
   `Min`/`Max` fall through to the `throw :157-158`.
2. `Math.Min/Max` (all numeric overloads each, plus `MathF`) share method name + 2-arg shape,
   so **one case each** covers everything; no overload explosion.
3. `SqlMethodCallNode(method, args)` (`Internal/SqlNodes.cs:90`) is generic:
   `BulkBatch.NormalizeComputedParameters:684-687` and all three generators' `CountMethodArgs`
   recurse into `Args` with **zero per-function code** — verified
   `SqlServerSqlGenerator.cs:415,425-434` (same pattern in Npgsql/Sqlite generators).
   New 2-arg nodes flow through untouched.
4. Nesting is free: `Min(Round(a/b,4), cap)` = `TranslateMethodCall(Min)` → `TranslateNode`
   on each arg → existing `Round` case (`SetExpressionTranslator.cs:150-151`) + column/parameter
   cases. Ternary wrapping either side also recurses (`TranslateConditional:60-67` → `CASE WHEN`
   on all 3 providers).
5. Static-only `Min(a,b)` (no entity reference) never reaches SQL: `:78-81` folds via `Evaluate()`
   to a parameter. Only column-involving calls need emission.
6. `ValidateComputedAssignmentType` already allows numeric widening — `Min(double,double)→double`
   passes; mixed-type pairs are the only question (see §3.3).

Governing rule (`docs/DESIGN.md` §8): per-provider capability — never hold back all providers
because one lacks support. Core builds provider-neutral nodes; each generator emits natively or
throws a provider-specific `NotSupportedException`. No silent rewrites with diverging semantics.

## 2. Changes (translator + compat option + 3 generators + messages)

1. **`SetExpressionTranslator.cs`**: add `case "Min" → new SqlMethodCallNode("LEAST", [arg0, arg1])`,
   `case "Max" → ("GREATEST", ...)` inside the existing `Math` block (declaring-type check
   unchanged, so `MathF` rides along). Update both supported-lists (`:28`, `:158`).
2. **Emission per provider** (`EmitMethod` in each generator — native or throw, never fallback):
   - Npgsql → `LEAST(a,b)` / `GREATEST(a,b)` (native, all versions, no gate).
   - SQLite → `min(a,b)` / `max(a,b)` (scalar multi-arg form, all versions, no gate).
   - SQL Server → `LEAST(a,b)` / `GREATEST(a,b)` **iff compat ≥ 160, else throw**
     provider-specific `NotSupportedException` (required: 160; how: set the option in §2.3 +
     `ALTER DATABASE <name> SET COMPATIBILITY_LEVEL = 160`). Explicitly **no `CASE WHEN`
     fallback** — mirrors EF Core (`GenerateLeast` returns null below 160; compat probing
     rejected in `dotnet/efcore#32528`): fail fast over silent semantic drift (`LEAST` ignores
     NULLs, `CASE WHEN` takes the `ELSE` branch; precedence/scale also differ).
3. **Compat mechanism** (new — our library has no compat concept today; verified zero hits
   outside test fakes):
   - New `BulkExecuteOptions.SqlServerCompatibilityLevel` (default **150**, matching EF Core's
     default). Flows through the existing per-`DbContext` channel (`UseBulkExecute(...)` →
     `BulkExecuteOptionsExtension` copy-in snapshot) and per-call explicit options (full
     replacement, existing contract). Tenant-safe (never a process-wide static).
     Applies to SQL Server generation only; ignored by Npgsql/SQLite.
   - `Clone`/`CopyTo`/`Validate` + `LogFragment`/`PopulateDebugInfo` updated alongside.
   - Internal `IBulkProvider.Generate(operations, maxParametersPerCommand)` widened to carry the
     level (internal interface: 3 providers + `BulkBatch` call site + fakes).
   - Explicitly rejected: `DbConnection.ServerVersion` (engine version ≠ per-database compat),
     runtime `sys.databases` query (sync generation path + per-tenant caching), process-wide
     static (wrong for tenants).
4. **No changes**: `SqlNodes`, `NormalizeComputedParameters`, `CountMethodArgs`,
   chunking/param budget.

## 3. Correctness investigations (must close before/with implementation)

1. **Explicit numeric casts**: `TranslateUnary` drops non-member `Convert` (`:181`) and marks
   member converts `ConvertedInTree` (`:179`). Audit what the generators emit for
   `ConvertedInTree` in arithmetic — if `(double)intCol / x` loses its cast, SQL integer-divides
   where C# double-divides. Decide: emit `CAST` on numeric-widening converts vs. status quo;
   pin with golden tests. (Plain `int/int` truncates in both languages — consistent; the
   cast-drop is the only hazard.)
2. **NULL semantics**: columns may be nullable. Emission is native per provider, so semantics
   are the DB's own — pin by live test per provider, then document (no guessing in code comments).
   Reference: SQL Server `LEAST` ignores NULLs unless all are NULL (verified in MS Learn).
3. **Mixed CLR types** (`Min(doubleCol, decimalCap)`): rely on DB coercion; add matrix tests;
   document PG's common-type requirement vs. SQL Server precedence vs. SQLite dynamic typing.
4. Confirm upsert `Update(selector, valueExpression)` shares this translator path (expected —
   cover with one upsert golden test).

## 4. Tests & docs

- Golden-SQL unit tests ×3 providers (`ComputedSetGoldenSqlTests` suites): column+static,
  static+column, column+column, nested `Min(Round(col/cap,4),cap)` + `Max` mirror,
  `Min` inside ternary branches (requestor's exact shape), static+static folding
  (asserts single parameter, no function in SQL), `Min`+`Max` nesting.
- SQL Server compat tests: default (150) → `NotSupportedException` with actionable message;
  160 → `LEAST`/`GREATEST` emitted; per-call options override per-`DbContext` default.
- Option plumbing tests: `Clone`/`CopyTo` independence, `Validate`, `LogFragment`/`PopulateDebugInfo`
  (mirror existing `BulkExecuteOptionsResolutionTests` patterns).
- Execution tests: SQLite integration (always runnable) + SQL Server/Npgsql container suites
  (`ComputedSetV2ExecutionTests` pattern): value assertions incl. fractional `value1/value2`,
  cap-hit vs. cap-miss rows, nullable-column rows.
- Docs: README supported-expression list, error strings, `NEXT_FEATURES_PROPOSAL.md §6` checkbox.

## 5. Open questions for review

1. (Resolved per rule: `LEAST` + compat-gated throw on SQL Server, native elsewhere. No `CASE WHEN`.)
2. SET-only, or also `Where`/guard predicates later (separate `LinqPredicateTranslator` work —
   propose follow-up, not this change)?
3. `float`/`decimal` scale expectations for `Round(x,4)` inside `Min` — assert exact
   CLR→store-type mapping per provider in tests, especially `decimal` on SQLite (float-affinity)
   vs. SQL Server (`LEAST` scale = highest-precedence arg)?
4. Where should the compat option live: on shared `BulkExecuteOptions` (applies to SQL Server
   only; simplest, follows existing per-`DbContext`+per-call channel) vs. a separate
   SQL-Server-specific options type?

## 6. Effort / risk

Effort: small-medium (1 translator hunk + 3 emitter branches + compat option + internal
`Generate` widening + tests). Risk: cast-drop (§3.1) and NULL semantics (§3.2) — both contained
by the test matrix; no chunking changes. Public-API addition: one option property (see §5 Q4).
