# Who owns the SQL Server compatibility level? (review document)

Status: **Option A implemented** (reviewer-approved) — the shared contract carries no
compat concept; resolution is private to `SqlServerProvider` (see §8).

## 1. The problem

Our library generates SQL per provider. SQL Server's `LEAST`/`GREATEST` (used for
`Math.Min`/`Math.Max`) requires compatibility level ≥ 160. Some piece of code must know the
effective level at generation time. The dispute: **where should that knowledge live** so the
shared, provider-neutral core never carries provider-specific concepts?

Two earlier decisions are settled and out of scope here:

- The level is **inherited from EF Core's configured `UseCompatibilityLevel`**, never probed
  from the server (`DbConnection.ServerVersion`, `sys.databases` queries — same rejections as
  EF itself, `dotnet/efcore#32528`).
- The library owns **no compat knob** on the shared `BulkExecuteOptions` (deleted; it was
  never shipped and could drift from EF's setting).

What remains open is only the shape of the internal plumbing (§6).

## 2. How EF Core handles this (plain version)

Short answer: **each provider keeps its own knobs in its own package. Nothing like them
exists in common.** There is no shared contract carrying `SqlServerCompatibilityLevel`,
`PostgresVersion`, or `SetPostgresVersion`.

### 2.1. Where each member lives (one row = one package)

| Member | Package | Type |
|---|---|---|
| `SqlServerCompatibilityLevel` | `Microsoft.EntityFrameworkCore.SqlServer` | `SqlServerOptionsExtension` (the provider's own options class) |
| `UseCompatibilityLevel()` | same SqlServer package | `SqlServerDbContextOptionsBuilder` (the provider's own builder) |
| `PostgresVersion` | `Npgsql.EntityFrameworkCore.PostgreSQL` | `NpgsqlOptionsExtension` (the provider's own options class) |
| `SetPostgresVersion()` | same Npgsql package | `NpgsqlDbContextOptionsBuilder` (the provider's own builder) |
| Common (`Microsoft.EntityFrameworkCore`, `.Relational`) | — | **none of the above exist here** |

Verified by a reflection scan over the actual `10.0.0` assemblies: every public
type/property containing `Compat` or `PostgresVersion` was listed per assembly. Common
core + Relational scored zero (the single text hit was the unrelated error message
`CoreStrings.IncompatibleSourcesForSetOperation` — about set operations, not compat levels).
The scan project was scratch and has been removed; it can be re-run on request.

### 2.2. The trick: common shell, provider-specific interior

The outer builder is shared, but the lambda parameter `b` is a **different,
provider-specific type** in each case:

```csharp
// SQL Server app — b is SqlServerDbContextOptionsBuilder (SqlServer package only):
options.UseSqlServer(connStr, b => b.UseCompatibilityLevel(160));

// Postgres app — b is NpgsqlDbContextOptionsBuilder (Npgsql package only):
options.UseNpgsql(connStr, b => b.SetPostgresVersion(16, 0));
```

There is no `options.UseCompatibilityLevel(...)` directly on the common builder. A Postgres
context can never see the SQL Server level and vice versa — the compiler makes it impossible,
because each `b` only exists where its provider package is referenced.

### 2.3. How EF consumes it at runtime

Same split. SQL Server's own translators/generators receive `ISqlServerSingletonOptions`
through DI; Npgsql's receive `INpgsqlSingletonOptions`. The common translator/generator
interfaces contain no compat/version members. Each value is configured per-`DbContext`
(`UseCompatibilityLevel`, EF 10 default 150 — confirmed by executing a probe context:
default `150`, explicit `160` → `160`) and read from the options snapshot at generation
time — never probed from the server.

### 2.4. Our repo already mirrors this packaging

| Our project | References | Sees EF's compat types? |
|---|---|---|
| `src/NSLabs.EFCore.Extensions` (core) | `EFCore.Relational` only | No — and must stay that way |
| `src/NSLabs.EFCore.Extensions.SqlServer` | `EFCore.SqlServer` | Yes |
| `src/NSLabs.EFCore.Extensions.Npgsql` | Npgsql provider | Yes (its own, if ever needed) |

So the EF-consistent placement for compat knowledge is: read it **inside
`NSLabs.EFCore.Extensions.SqlServer`** (the only place that can see EF's SqlServer types)
and keep the shared `IBulkProvider` contract free of it.

## 3. The .NET convention (provider model)

`System.Data.Common.DbCommand` carries no SqlClient/Npgsql members;
`SqlCommand` / `NpgsqlCommand` extend it inside their own assemblies. Common contracts carry
only neutral concepts (commands, connections, transactions); provider specifics live in
provider assemblies and are consumed there. EF's architecture (§2) is this same pattern
applied to an ORM.

## 4. Applicable principles (honest scope)

There is no ISO-style "industry standard" for this. The authorities that actually apply are:

- **ISP (Interface Segregation):** do not force implementers to inherit members they don't
  use. A compat member on the shared provider interface makes the SQLite/Npgsql providers
  "speak" SQL Server vocabulary.
- **Single source of truth:** one owner per fact (already applied: EF owns the level, we read it).
- **EF parity:** when building on EF, its layering (§2–§3) is the reference design, not an exception.

## 5. Our code — leak inventory and its resolution

Shared/neutral code that **used to** name the SQL Server concept (all removed under Option A):

- `IBulkProvider.cs` — `Generate(…, int sqlServerCompatibilityLevel)` (parameter *named*
  `sqlServer*` on the shared interface) and `int ResolveCompatibilityLevel(…) => 150`
  (default implementation inherited by SQLite/Npgsql)
- `BulkBatch.cs` — the call site passing the resolved level through

Current placement (verified — no `compat`/`SqlServer` identifier remains in
`src/NSLabs.EFCore.Extensions`):

- `IBulkProvider.Generate(operations, maxParametersPerCommand, DbContext context)` — neutral only
- `SqlServerProvider` (private method) — reads EF's `SqlServerOptionsExtension` per context,
  `EngineType` switch, missing/unconfigured → EF's `SqlServerOptionsExtension.SqlServerDefaultCompatibilityLevel`
  constant (150 on EF9/10, 160 on EF8/11 — flows per version, never hardcoded; on EF8 the
  constant is named `DefaultCompatibilityLevel`)
- `SqlServerSqlGenerator.Generate(…, compat)` — provider assembly taking a provider-specific
  parameter (same as EF's SqlServer-scoped services taking `ISqlServerSingletonOptions`)
- `BulkExecuteOptions` — no provider knobs (as required)

The `=> 150` default did not fix the leak; it only made it compile. The objection was upheld
and the members removed.

## 6. Options

### Option A — context flows, resolver goes private (recommended)

```csharp
// IBulkProvider.cs (shared): only neutral concepts.
// DbContext is already on ExecuteAsync; passing it here adds no new vocabulary.
IReadOnlyList<SqlChunkPlan> Generate(
    IReadOnlyList<BoundOperation> operations, int maxParametersPerCommand, DbContext context);
```

- `ResolveCompatibilityLevel` becomes a **private** method of `SqlServerProvider`; the
  default interface member is deleted; `SqlServerSqlGenerator` keeps its compat parameter.
- `BulkBatch` and the unit-test `Harness` use the identical call (`provider.Generate(ops, max, context)`),
  so tests keep exercising the real resolution path.
- The resolver-specific test file folds into the behavioral golden tests (default context
  throws, 160 emits — already covered in `MathMinMaxGoldenSqlTests`).
- Blast radius is compile-time only: the interface is internal (3 implementers + `Harness` +
  1 test stub). Zero public API impact.

How to implement it (concrete steps — this is the "same split" from §2.2, one layer down):

1. `IBulkProvider.cs` — revert to neutral members; delete the default `ResolveCompatibilityLevel` method entirely.
2. `SqlServerProvider.cs` — move today's resolver body into a **private** method (logic unchanged: read `SqlServerOptionsExtension` per context, `EngineType` switch, missing/unconfigured → EF's `SqlServerDefaultCompatibilityLevel` constant) and call it from its own `Generate` before delegating to `SqlServerSqlGenerator.Generate(ops, max, level)`.
3. `SqliteProvider` / `NpgsqlProvider` — accept the context parameter and ignore it (their generators take no compat input, exactly like today).
4. `BulkBatch.ExecuteCoreAsync` — `provider.Generate(_operations, options.MaxParametersPerCommand, _context)` instead of resolving an int first.
5. Unit-test `Harness` — the identical call (`provider.Generate(batch.Operations, max, context)`), so tests keep exercising the real resolution path: EF-default context throws for `Min`, `UseCompatibilityLevel(160)` context emits `LEAST`.
6. Tests — delete the resolver-specific file (`SqlServerCompatibilityLevelTests.cs`); its distinct cases are already covered behaviorally by `MathMinMaxGoldenSqlTests` (default-context throws with the `UseCompatibilityLevel` message; 159/160/170 gating).

EF8 default evidence (why 160, not 150 — verified three ways): the `v8.0.0` GA tag source
declares `DefaultCompatibilityLevel = 160`; the **shipped 8.0.31 binary executed on net8.0**
reports `DefaultCompatibilityLevel = 160` and `new SqlServerOptionsExtension().CompatibilityLevel = 160`;
and `dotnet/efcore#34316` ("Change default … to 150") explicitly frames 150 as a *change* from
the 160 default, justified as non-breaking precisely because "nothing generated by EF8 requires
160". A default can legitimately go *down* across majors: it was inert in EF8 (nothing EF8
generated needed 160) and only became behavioral in EF9 (160-gated OPENJSON translations),
which forced the flip to 150 so upgraders on older servers wouldn't break. EF11 then returned
the default to 160. Because we read EF's own default constant per version, our code is correct
on every major with no version table.

Resulting ownership, mirroring §2.4:

| Piece | Lives in | Sees compat? |
|---|---|---|
| `IBulkProvider`, `BulkBatch`, `BulkExecuteOptions` | core | No |
| Level read + `SqlServerSqlGenerator` compat param | SqlServer package | Yes |
| `MIN`/`MAX`, `LEAST`/`GREATEST` emission | Sqlite/Npgsql packages | Only their own (none needed) |

A future Npgsql version-gated need follows the identical pattern (read
`NpgsqlOptionsExtension` inside the Npgsql package) instead of landing on the shared
interface — the §2.2 precedent, not the §5 pattern.

### Option B — capability interface in core (considered, not recommended)

`IBulkProvider` stays clean, but core gains e.g. `ICompatibilityLevelAwareProvider` plus an
`is`-check branch in `BulkBatch`. This still names the SQL Server concept in neutral code
and adds runtime branching to save nothing over Option A. Weaker on both counts.

### Option C — status quo (not recommended)

Keep the default method. Costs: every current and future provider implementer inherits SQL
Server vocabulary, and it sets the precedent that the next provider-specific need (e.g. a
Npgsql `PostgresVersion` equivalent) also lands on the shared interface instead of in its
provider assembly — the exact pattern §2 shows EF avoiding.

## 7. Recommendation

Option A, because it is the only option under which **every** version/compat identifier in
the codebase lives in a provider assembly — i.e. the measured EF structure from §2,
reproduced one layer down.

## 8. Reviewer feedback (resolved)

- [x] Option A approved and implemented.
- [ ] Is anything in §2 factually wrong or missing (e.g. an EF common contract that *does*
      carry provider-specific members, which would weaken the argument)?
- [ ] Is the `Infrastructure.Internal` (EF1001) dependency acceptable with the current
      containment (SqlServer package only + pinned EF major), or should the read go through
      another channel?
- [x] Resolver unit tests folded into the behavioral golden tests (`MathMinMaxGoldenSqlTests`:
      default-context throws, 159/160/170 gating); resolver-specific file deleted.
