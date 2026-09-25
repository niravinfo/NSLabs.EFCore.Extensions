# P4 — Direct StringBuilder SQL Emission Design

**Date:** 2026-09-25  
**Status:** Approved for planning  
**Scope:** Internal SQL generation only; no public API changes

## Goal

Remove intermediate expression strings created by provider SQL emitters while preserving generated SQL, parameter order, provider-specific semantics, and existing operation/chunking behavior.

The refactor targets the allocation-heavy path:

```text
BoundOperation / SqlNode tree
    -> provider-specific emitter
    -> one final command StringBuilder
    -> one final command string
```

It does not change the relational model or translate user expressions differently.

## Non-goals

- No changes to `BulkBatch` binding or P3 entity-row matching.
- No changes to provider selection, execution, transaction, retry, or chunking behavior.
- No new global parameter-name cache.
- No optimization of `LargeListHelper` or other P16 micro-allocations.
- No SQL text or parameter-numbering changes.

## Chosen approach

Use the existing `StringBuilderCache`-owned chunk builder as the single output buffer. Change each provider's `ParameterEmitter` from returning `string` fragments to appending directly:

```csharp
void Emit(SqlNode node, IEntityType entityType, StringBuilder sql, ...);
void EmitValue(StringBuilder sql, object? value);
```

The three provider implementations remain separate. A shared writer abstraction is intentionally avoided because quoting, aliases, booleans, IN strategies, and function syntax are provider-specific.

## Provider changes

Apply the same structural change independently to:

- `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs`
- `src/NSLabs.EFCore.Extensions.Npgsql/Internal/NpgsqlSqlGenerator.cs`
- `src/NSLabs.EFCore.Extensions.Sqlite/Internal/SqliteSqlGenerator.cs`

For each provider:

1. `EmitStatement`, upsert emission, guards, and `EmitPredicate` pass the chunk builder through.
2. Every recursive `SqlNode` branch appends literals, operators, columns, and child output directly.
3. Nested method, logical, conditional, arithmetic, unary, IN, and NOT paths no longer create expression strings.
4. `EmitValue` appends the generated parameter name to the destination and records the same `SqlParam` in the same order.
5. Provider-specific quoting/alias helpers append directly where they are on the node-emission hot path; existing quote helpers remain available where a string return is required by non-emission callers.
6. Slow IN-list paths and multi-argument `CONCAT` paths append into the existing destination instead of acquiring a nested builder.
7. LIKE escaping, JSON/typed-array construction, and quoted table-name construction may still return value strings because those are values required by `SqlParam` or provider-specific helper APIs; they must not be used to rebuild a complete expression.
8. The outer builder is released through `StringBuilderCache` on both success and exceptions.

`EmitPredicate` must append ` AND ` directly for multiple predicate parts. It must not flatten logical nodes or change their parentheses.

## Parameter handling

The parameter counter remains per chunk. Emission order remains identical to the old recursive implementation, so names such as `@p0`, `@p1`, and so on remain stable.

The `SqlParam.Name` string must still be created because it is part of the returned plan. P4 does not add an unbounded static name cache. It removes the additional expression strings around that name while preserving deterministic numbering.

## Error and resource behavior

- Preserve existing `NotSupportedException` and `InvalidOperationException` conditions/messages.
- Do not return a partially generated plan.
- Release a cached outer builder if emission throws, matching the existing Npgsql/SQLite lifecycle and preventing pool contamination.
- Keep all existing guard-state transitions (`_inGuard`, provider budget flags) unchanged.

## Verification strategy

### Correctness

Run the existing provider golden suites byte-for-byte before and after the refactor. Add or extend focused cases for every emitter branch that can be affected:

- columns, parameters, booleans, and null checks;
- logical/comparison/arithmetic/unary expressions;
- `CASE`/`COALESCE` and computed assignments;
- methods including `CONCAT`, `LIKE`, and `LEAST`/`GREATEST` where supported;
- small and large IN paths, including negated and nullable variants;
- upsert values, guards, aliases, and target qualification;
- multiple predicate parts and TPH discriminators;
- SQL Server row-count declarations and chunk output.

Tests must assert SQL text and parameter values/order. All three unit provider suites and the existing integration suites must remain green.

### Performance

Use `Generation.SqlGenerationBenchmarks` with the existing `10`, `1,000`, and `10,000` operation parameters and the same `ShortRun` job before and after the change. Compare `Allocated` as the primary signal and `Mean` as a secondary signal. Do not compare different machines.

Success criteria:

- normalized SQL is unchanged;
- allocations decrease for chunk generation;
- no material regression in generation time;
- no regression in execution tests or parameter accounting.

## Rollback

P4 is isolated to internal generator implementation and tests. If exact SQL or provider behavior cannot be preserved, revert the provider changes without changing public API or the P2/P3 branches.
