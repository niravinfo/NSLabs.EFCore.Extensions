# Computed SET — Supported Operators & Functions Matrix

> Source: `src/NSLabs.EFCore.Extensions/Internal/SetExpressionTranslator.cs`, `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs`
> Design: isolated whitelist translator (no `EF Core` internals hijack). Any non-whitelisted node throws `NotSupportedException` with supported list.

## 1. Operators

| C# Expression | SQL Emitted (SQL Server) | Status | Notes |
|---|---|---|---|
| `x.Prop + y` / `x.Prop - y` / `*` / `/` / `%` | `([Prop] + @p)` `([Prop] * @p)` etc | **Supported (v1)** | `column op column`, `column op param`, `param op column`, `param op param` (collapsed). Includes `-x.Prop` unary. |
| `x.String + "_suf"` / `x.String + x.Other` | `([Name] + @p)` | **Supported (v2)** | `+` on `string` emits `+` (SQL Server). Null propagates (`NULL + 'a'` = `NULL`) matching EF `+`. `CONCAT` null-safe variant deferred. |
| `x.Amount ?? 0m` | `COALESCE([Amount], @p)` | **Supported (v2)** | `Coalesce` (`??`) via `SqlCoalesceNode`. Both sides may be column/param/method/conditional. |
| `cond ? a : b` | `CASE WHEN <test> THEN <a> ELSE <b> END` | **Supported (v2)** | `ConditionalExpression`. `Test` must be predicate-translatable subset (see §2). |
| `==` `!=` `<` `<=` `>` `>=` | `=`, `<>`, `<`, `<=`, `>`, `>=` | **Supported (v2) in `Test` / value** | Handles `IS NULL`/`IS NOT NULL` when comparing to `null`, `AND`/`OR`, boolean column `=1`. Value-converter aware. |
| `&&` `||` / `AndAlso` `OrElse` `!` | `(A AND B)` `(A OR B)` `NOT (...)` | **Supported (v2) in `Test`** | Only inside `Conditional.Test` or standalone boolean test. `!x.Active` -> `NOT ([Active]=1)`. |
| `Convert` / casts | unwrapped | **Supported** | Enum/numeric widening unwrapped; `ConvertedInTree` preserved for converter. |
| `&` `|` `^` `<<` `>>` bitwise | — | **Not supported (planned, low priority)** | Throw `NotSupportedException`. Use raw computed if needed. |
| `??=` / assignment | — | **Not supported** | — |

## 2. Conditional Test Predicate Subset

`CASE WHEN` `Test` reuses `LinqPredicateTranslator` shape:

* `x.Prop == value` / `!=` (with `IS NULL` handling)
* `x.Prop < value` etc (with converter)
* `x.Active` (bare `bool` -> `[Active]=1`)
* `x.Discount == null` / `!= null` -> `IS NULL` / `IS NOT NULL`
* `a && b`, `a || b`, `!a` (via `SqlNotNode` + `SqlBinaryNode And/Or`)
* Column-column comparisons `x.A == x.B` not in v1 but now allowed via new path

Not supported in `Test`: arbitrary `MethodCall` (e.g., `x.Name.Contains("a")`) — throw.

## 3. Methods / Properties

| C# | SQL | Status | Example |
|---|---|---|---|
| `x.Name.ToUpper()` | `UPPER([Name])` | **Supported (v2)** | |
| `x.Name.ToLower()` | `LOWER([Name])` | **Supported (v2)** | |
| `x.Name.Trim()` | `LTRIM(RTRIM([Name]))` | **Supported (v2)** | SQL Server `TRIM` compat via `LTRIM/RTRIM` |
| `x.Name.TrimStart()` | `LTRIM([Name])` | **Supported (v2)** | |
| `x.Name.TrimEnd()` | `RTRIM([Name])` | **Supported (v2)** | |
| `x.Name.Substring(s, len)` | `SUBSTRING([Name], s+1, len)` | **Supported (v2)** | C# 0-based `s` auto `+1`. 1-arg overload -> `SUBSTRING(col, s+1, LEN(col))` |
| `x.Name.Replace(old, new)` | `REPLACE([Name], @p, @p)` | **Supported (v2)** | |
| `string.Concat(a,b,...)` | `CONCAT(a,b,...)` | **Supported (v2)** | Static `string.Concat` |
| `x.Name.Length` | `LEN([Name])` | **Supported (v2)** | `Member` `Length` on `string` |
| `Math.Abs(x.Val)` | `ABS([Val])` | **Supported (v2)** | `Math`/`MathF` |
| `Math.Ceiling(x.Val)` | `CEILING([Val])` | **Supported (v2)** | |
| `Math.Floor(x.Val)` | `FLOOR([Val])` | **Supported (v2)** | |
| `Math.Round(x.Val)` / `Round(x.Val, n)` | `ROUND([Val],0)` / `ROUND([Val], n)` | **Supported (v2)** | |
| `Math.Truncate(x.Val)` | `ROUND([Val],0,1)` | **Supported (v2)** | SQL Server truncate via 3-arg `ROUND` |
| `DateTime.UtcNow` / `GETUTCDATE()` | `@p` (param) | **Not supported (deferred v3)** | Kept as captured param (`Evaluate`) to avoid clock skew. Documented difference vs EF `GETUTCDATE()`. |
| `EF.Functions.*` / `Json` / custom `x.Name.MyMethod()` | — | **Not supported (never whitelist)** | Throw `NotSupportedException: Method 'MyMethod' is not supported... Supported: ToUpper, ToLower, Trim...` |
| `x.Name.Contains / StartsWith` | — | **Not supported (not in SET, only Where)** | — |

## 4. Not Supported / Planned

| Feature | Status | Reason |
|---|---|---|
| Arbitrary `MethodCall` pass-through | Never | Injection, quoting, provider mismatch |
| `SetRaw("Price=Price*1.1")` | Rejected | Loses quoting `Quote()` |
| Computed `INSERT` values (`WHEN NOT MATCHED`) | Deferred | `Values(...)` remains constant; computed inserts require `s` alias |
| Server `DateTime` functions | Deferred v3 | Keep param in v2 |
| JSON path, bitwise, `string.IsNullOrEmpty` | Planned if requested | Add to whitelist + `EmitMethod` |

## 5. Provider Notes

* **SQL Server (v1/v2)**: `+` for string concat null-propagates; `CONCAT` null-safe alternative deferred. `CASE`/`COALESCE`/`UPPER`/`SUBSTRING`/`LEN` are native.
* **PostgreSQL/MySQL/SQLite (future)**: same `SqlNode` tree, provider emits `||`/`CONCAT`/ dialect-specific `TRIM`.

## 6. How to Check Support

* **Unit golden-SQL**: `tests/NSLabs.EFCore.Extensions.Tests.Unit/ComputedSetGoldenSqlTests.cs`
* **Integration**: `tests/NSLabs.EFCore.Extensions.Tests.Integration/ComputedSetExecutionTests.cs`
* Unsupported throws include whitelist in message — add new whitelist entry + `SqlMethodCallNode` + `EmitMethod` branch + `CountParameterNodes` + test.

