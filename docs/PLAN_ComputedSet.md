# Plan — Server-Side Computed `SET` (e.g. `Price * 1.1` without fetch)

> Status: **Draft for review** — no code changes yet.
> Goal: allow `Update` and `Upsert` `SET` assignments to be **SQL-translated expressions** referencing the current row value, so `price +10%` happens in the `UPDATE`/`MERGE` statement itself.
> Request: `we can't fetch the entity in the memory for its current value, it should be translated in the sql statement itself`

---

## 1. Problem Statement

Current API only supports **constant** assignments:

```csharp
// src/NSLabs.EFCore.Extensions/BulkOperationBuilders.cs:18,63
Set<TValue>(Expression<Func<TEntity,TValue>> selector, TValue value)
```

Stored as `BoundAssignment { IProperty, object? Value }` (`Internal/BoundOperation.cs:12`) and emitted as a parameter (`Internal/SqlServerSqlGenerator.cs:192,282` -> `SET [Price] = @p0`).

There is **no way** to express:

```sql
UPDATE [Products] SET [Price] = [Price] * 1.1 WHERE [Id] = @p1;
-- or MERGE WHEN MATCHED THEN UPDATE SET [Price] = [t].[Price] * 1.1
```

Required semantics: later operation sees earlier writes (sequential batch, `docs/DESIGN.md:28`), one round-trip, param-budget chunking respected.

EF Core parallel: `ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, x => x.Price * 1.1m))` — same shape we should mirror.

---

## 2. Goals / Non-Goals

### Goals (v1 of this feature)

* `Update` computed set: `Set(x => x.Price, x => x.Price * 1.1m)`, `x => x.Quantity + 1`, `x => x.Status + 1` etc., fully server-translated.
* `Upsert` computed set on `WHEN MATCHED`: clarify alias semantics (target `t` vs source `s`).
* SQL Server first (existing `MERGE`), design remains provider-neutral for PG/MySQL/SQLite future.
* Keep backwards compat — old `Set(selector, constant)` remains.
* Parameters remain parameterized; captured locals become `@pN` (`WHERE [Key1]=@p1` today in `LinqPredicateTranslator.cs:28`).
* Value converters still apply (enum → int, etc. via `ModelBinder.ConvertToProvider`).

### Non-Goals (defer)

* Arbitrary method calls (`Math.Round`, `string.Concat`) — only arithmetic + column refs + params in v1.
* Computed `INSERT` values for upsert `WHEN NOT MATCHED` — inserts are constants sourced from `Values(...)`; computed inserts would require `s` expression referencing other `s` columns (edge case).
* Staging-table fast path (`DESIGN.md:260`) — keep batched script strategy.
* Entity-style `Update(rows)` with per-row computations — those are materialized constants.

---

## 3. Desired API (proposed — open for review)

### 3.1 UpdateOperationBuilder

Keep existing, add overload:

```csharp
// src/NSLabs.EFCore.Extensions/BulkOperationBuilders.cs

// existing — constant
Set<TValue>(Expression<Func<TEntity,TValue>> selector, TValue value)

// NEW — computed, translated to SQL
Set<TValue>(Expression<Func<TEntity,TValue>> selector, Expression<Func<TEntity,TValue>> valueExpression)

// optional alias for discoverability (mirrors EF Core naming)
SetProperty<TValue>(Expression<Func<TEntity,TValue>> selector, Expression<Func<TEntity,TValue>> valueExpression)
```

Usage:

```csharp
await db.BulkExecuteAsync(b => {
    b.Update<Product>(op => op
        .Where(x => x.Id == 6)
        .Set(x => x.Price, x => x.Price * 1.1m));          // +10%

    b.Update<Product>(op => op
        .Where(x => x.Category == "Clearance")
        .Set(x => x.Price, x => x.Price * 0.9m)
        .Set(x => x.UpdatedAt, x => DateTime.UtcNow));     // mix constant & computed? see §3.3
});
```

Existing constant overload must still work — disambiguation via `Expression<>` vs `TValue`. Capture: `decimal factor = 1.1m; ... Set(x=>x.Price, x=> x.Price * factor)` → `factor` becomes parameter.

### 3.2 UpsertOperationBuilder — alias question (needs decision)

SQL Server `MERGE` has two row sources:

```sql
MERGE INTO [Products] WITH (HOLDLOCK) AS [t]
USING (VALUES (...)) AS [s] ([Id],[Price],...)
ON [t].[Id]=[s].[Id]
WHEN MATCHED THEN UPDATE SET [Price] = [t].[Price] * 1.1  -- existing row
-- vs
WHEN MATCHED THEN UPDATE SET [Price] = [s].[Price] * 1.1  -- incoming row
```

**Option A (Recommended, minimal): single-param, means `t`**

```csharp
u.On(x=>x.Code).Set(x=>x.Price, x=> x.Price * 1.1m).Values(new Product{Code="A", Price=100})
// translates to [t].[Price] * @pN
// To reference incoming value, use constant overload: Set(x=>x.Price, 110) or explicit s. — v2 adds two-param overload.
```

Pro: matches `Update` shape, covers 90% case (increment existing). Simple mental model. Document clearly: `x` in `valueExpression` for Upsert is `target` (`t`).

**Option B (More expressive, breaking ambiguity): two-param overload**

```csharp
Set<TValue>(Expression<Func<TEntity,TValue>> selector, Expression<Func<TEntity,TEntity,TValue>> valueExpression)
// x=t, s=source: Set(x=>x.Price, (t,s) => t.Price * 1.1m)
// or (t,s) => s.Price * 0.95m
```

Con: heavier API, needs two `ParameterExpression` in translator.

**Recommendation:** Ship Option A first, add Option B in follow-up if requested. Document that `WHEN MATCHED` computed SETs always read from `t` (the row being updated). Common upsert `+10%` is on `t`.

### 3.3 Mixing constant + computed in same op

Allow both overloads in same `Update`/`Upsert` — each call adds one `BoundAssignment` entry, now either constant or expression. Validate duplicate target column (`BulkBatch.cs:158`) still applies across both forms.

### 3.4 What NOT to add

No `Set(x=> x.Price * 1.1m)` without selector — keep selector explicit for column validation and store-generated checks (`ModelBinder.EnsureWritable`).

---

## 4. Translation Pipeline Changes

Current pipeline `DESIGN.md:209`: `Fluent model → Metadata binding → Grouping/Chunking → Provider strategy → DbCommand`

### 4.1 Model: `Internal/BoundOperation.cs:12`

```csharp
internal sealed class BoundAssignment {
    public required IProperty Property { get; init; }
    public required object? Value { get; init; }          // today: constant only
    // PROPOSED:
    public SqlNode? ValueExpression { get; init; }        // null => constant path
    // invariants: exactly one of Value / ValueExpression is set
    // alternative: keep Value as object? and add bool IsExpression + SqlNode Expr
}
```

Same for upsert `InsertValues` — those remain constant (sourced from entity instance via `ModelBinder.ReadMemberValue`), no change.

`Assignments` list already holds both Update and Upsert matched-update payloads (`BulkBatch.cs:169,251`). Both benefit.

### 4.2 Builder storage: `BulkOperationBuilders.cs:9,44`

```csharp
internal List<(LambdaExpression Selector, object? Value)> Sets  // today
// PROPOSED:
internal List<(LambdaExpression Selector, BoundSetValue Value)> Sets
// where BoundSetValue = discriminated union: Constant(object?) | Expression(LambdaExpression)
```

Simpler: keep two lists or store `LambdaExpression? ValueExpr` + `object? Constant`.

### 4.3 Binding: `BulkBatch.cs:146 BindUpdate`, `:196 BindUpsert`

* For constant path: `ModelBinder.CreateAssignment(property, value)` unchanged (`ModelBinder.cs:68`).
* For expression path:
  * Validate selector target not in conflict columns (upsert), not store-generated (`EnsureWritable`), not shadow.
  * Call new translator: `SqlNode expr = SetExpressionTranslator.Translate(valueExpression, entityType, valueExpression.Parameters[0], targetProperty)` — verify every `MemberExpression` is either valid column on `TEntity` or captured variable (param).
  * Ensure expression type is assignable to `property.ClrType` (allow implicit numeric conversions; fail fast on mismatch).
  * Run value-converter on parameter nodes inside expression? For computed `Status + 1` where `Status` is enum backed by int, column node already maps to `IProperty`; parameter conversion handled like predicate does (`LinqPredicateTranslator.cs:61` `ConvertToProvider`).
  * Store as `BoundAssignment{Property, ValueExpression=expr}`.

### 4.4 Expression translator — new or extend `LinqPredicateTranslator.cs`

Current translator only handles predicates (`Equal`, `AndAlso`, `OrElse`, `Not`, null checks, boolean). Computed SET needs:

* `MemberExpression` `x.Price` → `SqlColumnNode(property)` (no alias yet; alias injected at emit time).
* `ConstantExpression` / captured closure `factor` → `SqlParameterNode(value)` (evaluated via `Evaluate` like `LinqPredicateTranslator.cs:167`, `ModelBinder.ConvertToProvider` if needed).
* `BinaryExpression` `Add, Subtract, Multiply, Divide, Modulo`: → `SqlBinaryNode` with new operators (`Add`, `Subtract`, `Multiply`, `Divide`, `Modulo`) — extend `SqlBinaryOperator` enum (`SqlNodes.cs:24`).
* `UnaryExpression` `Negate`, `Convert/ConvertChecked` (numeric widening, enum): unwrap or preserve; if `x.Price` has `Convert`, mark `ConvertedInTree` like predicate does (`LinqPredicateTranslator.cs:37`).
* `ConditionalExpression` `x => x.Discount != null ? x.Price * 0.9m : x.Price` — defer (throw `NotSupportedException` in v1).
* `Coalesce` `x.Price ?? 0` — defer.

Scope v1: `Column [+ - * / %] Column`, `Column [+ - * / %] Param`, `Param [+ - * / %] Column`, `-Column`, `(a * b) + c` with parentheses. Parentheses emitted for correct precedence.

Out-of-scope v1 throws with clear message listing supported nodes — same style as `LinqPredicateTranslator.cs:29`.

**File decision:** Create `Internal/SetExpressionTranslator.cs` reusing helpers (`ResolveProperty`, `ReferencesEntity`, `Evaluate`, `ConvertToProvider`) rather than bloating predicate translator. Share `SqlNodes` and `ParameterEmitter`.

### 4.5 SQL Nodes: `Internal/SqlNodes.cs`

Extend:

```csharp
enum SqlBinaryOperator { Equal, NotEqual, LessThan, ..., Add, Subtract, Multiply, Divide, Modulo }
sealed class SqlUnaryNode(SqlNode inner, SqlUnaryOperator op) : SqlNode // Negate
// SqlArithmetic precedence handled in emitter
```

Reuse `SqlColumnNode`, `SqlParameterNode`, `SqlBinaryNode`.

### 4.6 SQL Generation: `Internal/SqlServerSqlGenerator.cs`

Current:

```csharp
// Update:  SqlServerSqlGenerator.cs:191-194
Append(Quote(col)).Append(" = ").Append(emitter.EmitValue(assignment.Value))

// Upsert matched:  SqlServerSqlGenerator.cs:278-282
Append(Quote(col)).Append(" = ").Append(emitter.EmitValue(assignment.Value))
```

Proposed:

```csharp
if (assignment.ValueExpression is not null)
    Append(emitter.Emit(assignment.ValueExpression, entityType, alias: null /*Update*/ or TargetAlias /*MERGE t*/))
else
    Append(emitter.EmitValue(assignment.Value))
```

* Update: alias `null` → `[Price]` (unqualified; emitted inside `UPDATE [T] SET` without alias). Alternative is to not alias — SQL Server allows `SET [Price]=[Price]*@p0`.
* Upsert `WHEN MATCHED`: alias `TargetAlias` (`t`) → `[t].[Price] * @p0`. Document this. If Option B (t,s) is chosen, emitter must handle two aliases; that requires `SqlSourceColumnNode` or parameterized alias.

Emitter changes (`ParameterEmitter.Emit`):

* Add cases for arithmetic binary operators: `Add => "+"`, `Subtract => "-"`, `Multiply => "*"`, `Divide => "/"`, `Modulo => "%"`.
* Arithmetic not parenthesized as predicate booleans are; need `(Left) * (Right)` rules: emit as `({Left} * {Right})` when child is binary to avoid precedence bugs, or minimal `Left * Right` with wrapping when needed. Simplest: always `(...)` for arithmetic children — verify golden SQL.
* `Emit` already handles `SqlColumnNode` with optional `alias` (`SqlServerSqlGenerator.cs:355`). Reuse.
* `CountParameterNodes` (`SqlServerSqlGenerator.cs:331`) must traverse new arithmetic nodes; already switches on `SqlBinaryNode`, `SqlNotNode`, `SqlParameterNode` — will count correctly once new opcodes added. Also count inside `ValueExpression`.
* `CountParameters` (`320`) sums `Assignments.Count` today — change to sum `CountParameterNodes(assign.ValueExpression) ?? 1` for expression case (if expression has no params, e.g. `x.Price + x.Price`, still 0 params, but operation still valid).
* Chunk param budget (`ExpandUpsert`, `Generate`) uses same `CountParameters` — expression params count correctly.

### 4.7 Value Converters & Type Mapping

Like predicate path (`LinqPredicateTranslator.cs:61`), if `SqlColumnNode.ConvertedInTree` is set (enum conversion), the sibling parameter already evaluated to provider value. For `x.Price * factor` where `Price` is `decimal` and `factor` is `decimal`, `ConvertToProvider` not needed (no converter). For `x.Status` enum arithmetic (e.g. int-backed), column stays enum property but arithmetic on int — allow; at emit time column name resolved via `ModelBinder.GetColumnName`, no conversion applied to column (converter is for values, not expressions). Parameter side already converted.

Need to decide: arithmetic between `decimal` column and `double` param — SQL Server implicit conversion; we emit as-is, let server coerce. Validate types are numeric or throw.

---

## 5. SQL Examples (golden)

### Update — single computed set

```csharp
b.Update<Product>(op=>op.Where(x=>x.Id==6).Set(x=>x.Price, x=>x.Price * 1.1m))
```

```sql
DECLARE @rc0 int;
UPDATE [Products] SET [Price] = ([Price] * @p0) WHERE [Id] = @p1;
SET @rc0 = @@ROWCOUNT;
SELECT @rc0 AS Op0;
-- @p0=1.1m, @p1=6
```

Mixed:

```csharp
b.Update<Product>(op=>op.Where(x=>x.Id==1).Set(x=>x.Price, x=>x.Price * @factor).Set(x=>x.Stock, 5))
```

```sql
UPDATE [Products] SET [Price] = ([Price] * @p0), [Stock] = @p1 WHERE [Id] = @p2;
```

### Upsert — MATCHED computed

```csharp
b.Upsert<Product>(u=>u.On(x=>x.Code).Set(x=>x.Price, x=>x.Price * 1.1m).Values(new Product{Code="A", Price=100, Stock=1}))
```

```sql
MERGE INTO [Products] WITH (HOLDLOCK) AS [t]
USING (VALUES (@p0,@p1,@p2)) AS [s] ([Code],[Price],[Stock])
ON [t].[Code]=[s].[Code]
WHEN MATCHED THEN UPDATE SET [Price] = ([t].[Price] * @p3)
WHEN NOT MATCHED THEN INSERT ([Code],[Price],[Stock]) VALUES ([s].[Code],[s].[Price],[s].[Stock]);
```

Guard still works: `WHEN MATCHED AND [t].[Active]=1 THEN UPDATE SET [Price]=([t].[Price]*@p3)`.

---

## 6. Validation & Error Handling

* `Set(selector, expr)` where `expr` does NOT reference entity (e.g. `x=> 5m`) → still allowed? Treat as constant-like but via expression; emit `@p0`. Prefer throw hint: use constant overload.
* Assignment to store-generated / key / shadow → `InvalidOperationException` (`ModelBinder.EnsureWritable`).
* Upsert `Set` targeting conflict column → same error as today (`BulkBatch.cs:238`).
* Duplicate column assignments across overloads → same check (`BulkBatch.cs:162,244`).
* Unsupported expression node (method call, member not on entity, nested lambda) → `NotSupportedException` with node type and example supported patterns.
* Division by zero is runtime SQL error, not validation.

---

## 7. Provider Considerations

* **SQL Server (v1):** Batched `UPDATE`/`MERGE` script handles arithmetic directly. No `HOLDLOCK` change.
* **PostgreSQL/MySQL/SQLite (future):** Same `ValueExpression` model maps to `DO UPDATE SET col = col * EXCLUDED.col` etc. Keep `BoundAssignment.ValueExpression` provider-agnostic; provider decides alias (`EXCLUDED` for PG). No design debt.
* **SQLite sequential mode:** same script split, inside transaction — works.

---

## 8. Tests & Docs

### Unit (golden-SQL)

* `UpdateGoldenSqlTests.cs` additions:
  * `Computed_price_multiply_by_factor`
  * `Computed_capture_local_variable`
  * `Mixed_constant_and_computed`
  * `Two_updates_one_computed_one_constant_share_counter`
  * `Enum_status_increment` if supported
  * `Negate_and_add`
  * `Throws_on_unsupported_method_call`
* `UpsertGoldenSqlTests.cs`:
  * `Matched_computed_references_target_alias`
  * `Matched_computed_with_guard`
  * `Mixed_upsert_update_chunk_counts_params`

### Integration (Testcontainers)

* `UpdateExecutionTests.cs`: seed `Price=100`, run `Set(x=>x.Price, x=>x.Price*1.1m)`, assert `110`, verify sequential semantics (two ops on same row, second sees first).
* Upsert: insert then upsert with computed matched update, assert target incremented not source.

### Docs

* `docs/DESIGN.md` §2 Style A update table, §3 Translation Pipeline add arithmetic.
* `README.md` quick-start show `Set(x=>x.Price, x=>x.Price*1.1m)` example.

---

## 9. Implementation Steps (incremental, no breaking changes)

1. **Model:** Add `BoundAssignment.ValueExpression` (`Internal/BoundOperation.cs`), overload storage in `BulkOperationBuilders.cs`.
2. **Builder:** Add `Set(selector, Expression<Func<TEntity,TValue>>)` overloads to both builders, keep old.
3. **Translator:** New `SetExpressionTranslator` (or extend `LinqPredicateTranslator` helpers) + extend `SqlNodes.cs` operators.
4. **Binder:** Update `BulkBatch.BindUpdate/BindUpsert` to handle both constant and expression assignments.
5. **Generator:** Update `SqlServerSqlGenerator.EmitStatement/EmitMerge`, `CountParameters/CountParameterNodes`, `ParameterEmitter.Emit` for arithmetic.
6. **Tests:** Add golden + integration tests, run `dotnet test` unit (no Docker) then integration where Docker available.
7. **Docs:** Update `DESIGN.md`/`README.md`.

Estimated diff: 5 files touched + 1 new translator, <400 LOC.

---

## 10. Open Questions for Review

1. **Upsert alias:** Option A (`x` = `t`) vs Option B (`(t,s) => ...`) — confirm with team.
2. **Do we need `Set(x=>x.Price, x=>x.Price + x.Cost)` (column-to-column)?** Proposed yes (v1 includes).
3. **Should we support `Increment` sugar:** `SetIncrement(x=>x.Qty, 1)` / `SetMultiply(x=>x.Price, 1.1m)` for ergonomics?
4. **Null handling:** `Price * factor` where `Price` nullable — SQL `NULL * @p` = `NULL`. Document; `COALESCE` deferred.
5. **Decimal precision/scale:** Let SQL Server handle; need test for `decimal` mapping.
6. **Compatibility with `MaxParametersPerCommand` chunking:** Expression with 0 params still valid; confirm counting.

---

## 11. Alternatives Considered (rejected)

* **String SQL fragment:** `SetRaw("Price = Price * 1.1")` — injection risk, loses metadata quoting/type safety.
* **Two-phase fetch+update:** Violates requirement (extra round-trip, race).
* **EF Core `ExecuteUpdate` delegation per op:** Loses single round-trip batching guarantee.
* **Reuse predicate translator for SET:** Predicate logic is boolean/comparison; arithmetic needs distinct operator set — separate translator clearer.
