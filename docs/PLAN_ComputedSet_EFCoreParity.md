# Plan — Server-Side Computed `SET` EFCore Parity (v2)

> Status: **Final for build** — approved, no hijack of EF Core internals.
> Goal: extend `v1` arithmetic `SET` (`Internal/SetExpressionTranslator.cs:89`, `Internal/SqlNodes.cs:24`) to the subset of `EF Core ExecuteUpdate` patterns that is **translatable with our own isolated translator** and remains `easy + error-prune`. Explicitly **not** reusing `EF Core` `RelationalSqlTranslatingExpressionVisitor` / `ToQueryString` stealing.
> Prior art: `docs/PLAN_ComputedSet.md` (v1 shipped: `Update`/`Upsert` `Set(x=>x.C, x=> x.C * 1.1)`).

---

## 1. Problem Statement

v1 done supports (`src/NSLabs.EFCore.Extensions/Internal/SetExpressionTranslator.cs:17`):

```csharp
Set(x => x.Amount, x => x.Amount * 1.1m) // Binary Add/Sub/Mul/Div/Mod
Set(x => x.Key2, x => -x.Key2)            // Unary Negate
Set(x => x.Key2, x => x.Key2 + x.Key3)    // column-column
```

EF Core `ExecuteUpdate` supports **any** provider-translatable expression:

```csharp
db.Orders.ExecuteUpdate(s => s
  .SetProperty(o => o.Name, o => o.Name + "_suf")              // string concat
  .SetProperty(o => o.Name, o => o.Name.ToUpper())             // method -> UPPER
  .SetProperty(o => o.Amount, o => o.Discount == null ? o.Amount : o.Amount * 0.9m) // CASE
  .SetProperty(o => o.Amount, o => o.Amount ?? 0)              // COALESCE
  .SetProperty(o => o.UpdatedAt, o => DateTime.UtcNow)         // stays param in v1, EF maps to GETUTCDATE()
);
```

Bulk batch lacks these. Users expect parity. Full parity via **hijacking EF translation pipeline** (`ISqlTranslatingExpressionVisitorFactory`, `QueryCompilationContext`, `SelectExpression`, `ToQueryString` parsing, `TargetAlias=t` rewrite) is **not easy / not error-prune**: `internal` API breaks every `10.0.x`, param naming unstable, `HoldLock` `MERGE` alias handling fragile, `ValueConverter` double-translate.

Decision: **Extend isolated translator** (`SetExpressionTranslator`) with a **whitelisted, test-covered subset** that matches EF for `~95%` of `SET` use-cases and stays `~350 LOC`.

---

## 2. Goals / Non-Goals (v2)

### Goals (ship)

* Same public API — no new overloads. Existing `BulkOperationBuilders.cs:18,63` `Set<T>(selector, Expression<Func<TEntity,TValue>>)` already accepts any `Expression`, only translator gate widens.
* Provider-neutral `BoundAssignment.ValueExpression:SqlNode` (`Internal/BoundOperation.cs:12`) stays.
* SQL Server first, PG/MySQL/SQLite design untouched.
* Add to translator, with explicit `SqlNode`/`SqlServerSqlGenerator` mapping:
  1. **String concat** `+` on `string` + `ToUpper`/`ToLower`/`Trim`/`Substring`/`Replace`/`string.Concat` (maps to `+`/`UPPER`/`LOWER`/`LTRIM(RTRIM)`/`SUBSTRING`/`REPLACE`/`CONCAT`).
  2. **Conditional** `ConditionalExpression` `cond ? ifTrue : ifFalse` -> `CASE WHEN`.
  3. **Coalesce** `x.Price ?? 0m` / `CoalesceExpression` -> `COALESCE`.
  4. **Null-aware arithmetic** stays `NULL * @p = NULL` semantics (document).
  5. Keep `Arithmetic`, `Negate`, `Convert`, `captured @p`, `column` existing.
* Keep one-round-trip, sequential batch `docs/DESIGN.md:28`, param-budget chunking `SqlServerSqlGenerator.cs:320`.

### Non-Goals (defer, or never hijack)

* Arbitrary `MethodCall` blind pass-through (e.g. `JsonDocument.Parse`). Whitelist only.
* `DateTime.UtcNow` -> `GETUTCDATE()` server function. v2 keeps it as **parameter** (`Evaluate:159` -> `SqlParameterNode`) to avoid clock skew debate; add function mapping in v3 if requested.
* Computed `INSERT` `WHEN NOT MATCHED` values (`Values(...)` remains constant).
* Reusing `Microsoft.EntityFrameworkCore.Query` internals. Rejected: version-fragile, alias bug surface.
* `SetRaw("Price=Price*1.1")` string fragment — injection risk.

---

## 3. Desired API (no change — translator widens)

```csharp
// existing overload covers all, no new API
Set<TValue>(Expression<Func<TEntity,TValue>> selector, Expression<Func<TEntity,TValue>> valueExpression)
SetProperty<TValue>(..., Expression<Func<TEntity,TValue>> valueExpression) // alias
```

Examples now valid in v2:

```csharp
await db.BulkExecuteAsync(b => {
  b.Update<Order>(op => op.Where(x=>x.OrderNo=="O-1")
    .Set(x=>x.Name, x=> x.Name + "_suffix")                          // string +
    .Set(x=>x.Name, x=> x.Name.ToUpper())                             // UPPER
    .Set(x=>x.Amount, x=> x.Discount == null ? x.Amount : x.Amount * 0.9m) // CASE
    .Set(x=>x.Amount, x=> x.Amount ?? 0m)                             // COALESCE
    .Set(x=>x.Amount, x=> (x.Amount + x.Fee) * factor)                // mixed still
  );
  b.Upsert<Customer>(u=>u.On(x=>x.Code)
    .Set(x=>x.Name, x=> x.Name + "_upd") // -> [t].[Name] + @p
    .WhenMatched(x=> !x.Active)
    .Values(...));
});
```

Upsert alias rule unchanged: `x` in `valueExpression` is target `t` (`SqlServerSqlGenerator.cs:288` `TargetAlias`). Document.

---

## 4. Translation Pipeline Changes (delta over v1)

Pipeline unchanged `DESIGN.md:209`: `Fluent -> Metadata binding BulkBatch.cs:146/196 -> Grouping/Chunking -> Provider strategy -> DbCommand`.

### 4.1 Model `Internal/BoundOperation.cs:12`, `Internal/SqlNodes.cs:1`

Extend:

```csharp
// SqlNodes.cs
enum SqlBinaryOperator { ..., Add, Subtract, Multiply, Divide, Modulo, StringConcat } // Add doubles as StringConcat when operand type string
sealed class SqlConditionalNode(SqlNode Test, SqlNode IfTrue, SqlNode IfFalse) : SqlNode
sealed class SqlCoalesceNode(SqlNode Left, SqlNode Right) : SqlNode // or reuse Binary COALESCE
sealed class SqlMethodCallNode(string Method, IReadOnlyList<SqlNode> Args) : SqlNode // ToUpper etc, or dedicated Upper/Lower nodes
// Alternative: dedicated SqlUpperNode(SqlNode Inner) for error-prune whitelist

// SqlBinaryOperator already has Add; reuse with type check string vs numeric
```

No `BoundAssignment` change.

### 4.2 Binding `BulkBatch.cs:146 BindUpdate`, `:196 BindUpsert`

No API change; `SetExpressionTranslator.Translate` now returns richer nodes. Keep `ValidateComputedAssignmentType:414` (numeric/string/nullable assignable check widens to `string`).

`NormalizeComputedParameters` stays enum->underlying `SetExpressionTranslator.cs:166`.

### 4.3 Translator `Internal/SetExpressionTranslator.cs:15` (core delta)

Keep helpers `ResolveProperty`, `ReferencesEntity:134`, `Evaluate:159`, `NormalizeParamValue:166`.

Add cases in `TranslateNode:15`:

* `ConditionalExpression cond => new SqlConditionalNode(Translate(cond.Test), Translate(cond.IfTrue), Translate(cond.IfFalse))`
  * `Test` expected to be predicate-translatable subset (`Equal`, `NotEqual`, `AndAlso`, `OrElse`, `Not`, `IsNull`, boolean column). Reuse `LinqPredicateTranslator` shape for test, but emit via `CASE` not `WHERE`. Validate test references entity; else evaluate whole conditional as param (captured bool?).
* `BinaryExpression { NodeType: Coalesce } => new SqlCoalesceNode(left,right)`
* `BinaryExpression Add` where `left.Type==string || right.Type==string` => treat as `StringConcat` (SQL `+` / `CONCAT` — pick `+` for SQL Server, same as EF). Keep arithmetic `Add` when numeric.
* `MethodCallExpression call`
  * Whitelist:
    ```csharp
    call.Method.DeclaringType == typeof(string) && call.Method.Name is "ToUpper" or "ToLower" or "Trim" or "TrimStart" or "TrimEnd" or "Substring" or "Replace" or "Concat"
    call.Method.DeclaringType == typeof(Math) && call.Method.Name is "Abs" or "Ceiling" or "Floor" or "Round" or "Truncate" // numeric
    ```
  * For `x.Name.ToUpper()` => `SqlMethodCallNode("UPPER", [columnNode])`; for `string.Concat(a,b)` => `SqlMethodCallNode("CONCAT", [left,right])`; for `Math.Abs(x.Key2)` => `SqlMethodCallNode("ABS", [col])`.
  * If `!ReferencesEntity(call, entityParameter)` -> `Evaluate` to param (captured `Math.Round(factor)`).
  * Else unsupported method -> `NotSupportedException` with whitelist message (style `TranslateBinary:130`).
* `MemberExpression` extension: `x.Name.Length` -> `LEN([Name])` (optional v2, easy). Defer `DateTime.UtcNow` server mapping; keep as param via `!ReferencesEntity` -> param (current behavior). Document difference vs EF.

Keep `TranslateMember:27`, `TranslateUnary:49` (Convert, Negate), `TranslateBinary:89` (Add/Sub/Mul/Div/Mod + string Add).

`ReferencesEntity` already handles `MethodCall`, `Conditional`, `Binary`, `Member` — no change.

### 4.4 SQL Generation `Internal/SqlServerSqlGenerator.cs:350 ParameterEmitter`

Extend `Emit:358`:

```csharp
SqlConditionalNode c => $"CASE WHEN {Emit(c.Test, entityType, alias)} THEN {Emit(c.IfTrue, ..., alias)} ELSE {Emit(c.IfFalse, ..., alias)} END"
SqlCoalesceNode co => $"COALESCE({Emit(co.Left, ..., alias)}, {Emit(co.Right, ..., alias)})"
SqlMethodCallNode m => m.Method switch {
  "UPPER" => $"UPPER({Emit(m.Args[0], ..., alias)})",
  "LOWER" => $"LOWER({Emit(m.Args[0], ..., alias)})",
  "TRIM"  => $"LTRIM(RTRIM({Emit(m.Args[0], ..., alias)}))",
  "SUBSTRING" => $"SUBSTRING({Emit(m.Args[0], ..., alias)}, {Emit(m.Args[1], ..., alias)}, {Emit(m.Args[2], ..., alias)})",
  "REPLACE" => $"REPLACE({Emit(m.Args[0], ..., alias)}, {Emit(m.Args[1], ..., alias)}, {Emit(m.Args[2], ..., alias)})",
  "CONCAT" => $"CONCAT({string.Join(", ", m.Args.Select(a=>Emit(a,...,alias)))})",
  "ABS" => $"ABS({Emit(m.Args[0], ..., alias)})",
  _ => throw
}
SqlBinaryNode { Operator: StringConcat } => $"({Emit(Left)} + {Emit(Right)})" // or CONCAT
```

Arithmetic keeps `({Left} * {Right})` parens `Emit:369`.

`CountParameterNodes:338` add:

```csharp
SqlConditionalNode c => Count(c.Test)+Count(c.IfTrue)+Count(c.IfFalse)
SqlCoalesceNode co => Count(co.Left)+Count(co.Right)
SqlMethodCallNode m => m.Args.Sum(Count)
```

`CountParameters:320`/`fixedCost:79` already sum `CountParameterNodes(ValueExpression)` — no change.

Alias handling unchanged: `Update` `alias:null` -> `[Col]`, `Upsert` `alias:TargetAlias` -> `[t].[Col]`.

### 4.5 Value Converters / Type Mapping

Same as v1 `ModelBinder.ConvertToProvider:45`. For `string` ops no converter; for `Conditional` branches may have different provider types — rely on server implicit conversion, validate `ValidateComputedAssignmentType` allows `string` to `string`, `decimal?` to `decimal?`.

`DateTime` stays param; no `GETUTCDATE()`.

---

## 5. SQL Examples (golden)

### Update — string + conditional + coalesce

```csharp
b.Update<Order>(op=>op.Where(x=>x.OrderNo=="O-1")
  .Set(x=>x.Name, x=> x.Name + "_suffix")
  .Set(x=>x.Amount, x=> x.Amount ?? 0m)
  .Set(x=>x.Amount, x=> x.Discount == null ? x.Amount : x.Amount * 0.9m)
  .Set(x=>x.Name, x=> x.Name.ToUpper()));
```

```sql
UPDATE [Orders] SET
  [Name] = ([Name] + @p0),
  [Amount] = COALESCE([Amount], @p1),
  [Amount] = CASE WHEN [Discount] IS NULL THEN [Amount] ELSE ([Amount] * @p2) END,
  [Name] = UPPER([Name])
WHERE [OrderNo]=@p3;
-- @p0='_suffix', @p1=0, @p2=0.9
```

Mixed arithmetic+conditional:

```sql
UPDATE [Items] SET [Key2] = CASE WHEN ([Status] = @p0) THEN ([Key2] + @p1) ELSE ([Key2] - @p2) END WHERE [Id]=@p3;
```

### Upsert — MATCHED with CASE

```csharp
b.Upsert<Customer>(u=>u.On(x=>x.Code)
  .Set(x=>x.Name, x=> x.Name + "_upd")
  .WhenMatched(x=> x.Active)
  .Values(...));
```

```sql
MERGE INTO [Customers] WITH (HOLDLOCK) AS [t]
USING (VALUES (@p0,@p1)) AS [s] ([Code],[Name]) ON [t].[Code]=[s].[Code]
WHEN MATCHED AND [t].[Active]=1 THEN UPDATE SET [Name] = ([t].[Name] + @p2)
WHEN NOT MATCHED THEN INSERT ...
-- [t] alias proves target
```

---

## 6. Validation & Error Handling

* Store-generated / conflict / duplicate checks unchanged `BulkBatch.cs:158,238`, `ModelBinder.EnsureWritable`.
* Unsupported `MethodCall` (e.g. `x.Name.CustomMethod()`) -> `NotSupportedException: "Method 'CustomMethod' is not supported in computed SET. Supported: ToUpper, ToLower, Trim, Substring, Replace, Concat, Abs, Ceiling, Floor, Round."`
* `Conditional` with non-boolean test or `Coalesce` with incompatible types -> `NotSupportedException`.
* `Add` on `string` vs numeric distinguished by `operand.Type`; invalid `string * int` -> `NotSupportedException`.
* Whole expression no entity ref (`x=> 5`, `x=> "a"+"b"`) -> collapses to `SqlParameterNode(Evaluate)`, emits `@p0` — allowed, hint to use constant overload.
* Division by zero runtime, not validation.

---

## 7. Provider Considerations

* **SQL Server**: `+` for concat (null yields null; `COALESCE`/`CONCAT` null-safe differs — document `+` keeps null propagation like ` Price+Fee` ; `CONCAT` alternative `CONCAT_WS` v3).
* **PostgreSQL/MySQL/SQLite**: same `SqlNode` tree; provider emits `||`/`CONCAT`/`CASE`/`COALESCE` per dialect. No design debt.
* **Chunking**: `CountParameterNodes` covers new nodes; `MaxParametersPerCommand` respects conditional/coalesce/method params.

---

## 8. Tests & Docs

### Unit (golden-SQL) `ComputedSetGoldenSqlTests.cs`

Add:

* `String_concat_plus`
* `String_to_upper_lower_trim`
* `String_concat_method`
* `Math_abs_round`
* `Conditional_case_when`
* `Coalesce`
* `Conditional_with_arithmetic`
* `Throws_on_unsupported_custom_method`
* `Upsert_string_concat_targets_t_alias`

### Integration `ComputedSetExecutionTests.cs`

Add (Testcontainers `SqlServerFixture:7`):

* Seed `Name="Base"`, run `Set(x=>x.Name, x=> x.Name + "_suf")` assert `"Base_suf"`
* `ToUpper`/`ToLower`
* `Amount ?? 0` where `Amount` null
* `Discount==null ? Amount*0.9 : Amount` both branches
* `Upsert` string concat `WHEN MATCHED` sequential semantics (second operation sees first)
* Enum `CASE` mixed

### Docs

* `docs/DESIGN.md:209` pipeline add `Conditional/Coalesce/MethodCall`
* `README.md` quick-start add string/conditional example
* Keep `docs/TRANSACTIONS.md` sequential semantics note.

---

## 9. Implementation Steps (incremental, <350 LOC, no breaking)

1. **Nodes** `Internal/SqlNodes.cs:24` add `SqlConditionalNode`, `SqlCoalesceNode`, `SqlMethodCallNode` (or fine-grained `SqlUpperNode` etc).
2. **Translator** `Internal/SetExpressionTranslator.cs` add `Conditional`, `Coalesce`, `MethodCall` whitelist, `StringConcat` branch in `TranslateBinary`.
3. **Generator** `Internal/SqlServerSqlGenerator.cs:350` add `Emit` cases, `CountParameterNodes` branches, `Render` helpers.
4. **Binder** `BulkBatch.cs:414` widen `ValidateComputedAssignmentType` to `string`.
5. **Tests** golden + integration as §8, run `dotnet test` unit (no Docker) then integration.
6. **Docs** `DESIGN.md`/`README.md`.

Existing v1 arithmetic tests must stay green.

---

## 10. Open Questions (Resolved)

* **Hijack EF** vs **extend own translator** — **Rejected hijack** (§11) for error-prune.
* **DateTime server function** — Keep param in v2; server `GETUTCDATE()` opt-in v3.
* **String `+` null handling** — Use `+` (SQL Server null propagates) to match EF `+` behavior; `CONCAT` null-safe variant deferred.
* **Substring 0/1-based** — Validate EF maps `Substring(0,2)` -> `SUBSTRING(col,1,2)`; we mirror via `+1` if needed (test).

---

## 11. Alternatives Considered (Rejected)

* **Hijack `RelationalSqlTranslatingExpressionVisitor`/`ToQueryString`** — version-fragile, `internal`, param/alias bug surface, not `easy`.
* **`SetRaw("Name = Name + '_a'")`** — injection, loses quoting `Quote:345`.
* **Per-op `ExecuteUpdateAsync` delegation** — loses one-round-trip batching `DESIGN.md:28`.
* **Full `EF` translator port** — >200 translators, heavy, still needs `t` alias pass.

