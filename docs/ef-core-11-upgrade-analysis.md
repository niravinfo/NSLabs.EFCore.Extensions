# EF Core 11 / .NET 11 upgrade impact analysis

Analysed 2026-11-21 while preparing the side-by-side `net10.0` / `net11.0` build.
Scope: this library's own code, its three provider packages, its tests/samples,
and the packages it consumes. Sources: official EF Core 11 "What's new" and
"Breaking changes" docs, official .NET 11 breaking-changes index, Npgsql 11
release notes, and the EF 10.0.15 / 11.0.0 `SqlServerOptionsExtension` source.

## 1. The change that actually matters: SQL Server compatibility level

EF Core 11 changes `UseSqlServer(...)`'s default compatibility level from **150**
(EF 10) to **160** (EF 11). Verified directly in the EF source:

| EF version | `SqlServerDefaultCompatibilityLevel` | `AzureSqlDefaultCompatibilityLevel` |
|---|---:|---:|
| 10.0.15 | 150 | 170 |
| 11.0.0 | **160** | 170 |

`SqlServerProvider.ResolveCompatibilityLevel` deliberately reads
`SqlServerOptionsExtension` rather than owning a library knob, so the library
inherits this change automatically. Consequences:

- `Math.Min` / `Math.Max` in computed `SET` expressions now emit `LEAST` /
  `GREATEST` by default on `net11.0`. On `net10.0` they still throw the
  actionable "set compatibility level 160" exception.
- `SqlServerSqlGenerator.EmitLeastGreatest` already enforces `>= 160` with no
  `CASE WHEN` fallback (correct: `LEAST` ignores NULLs), so no code change was
  needed — only the one golden-SQL test whose premise ("default throws") is
  TFM-dependent. It is now split into a `net10.0` "throws" case and a `net11.0`
  "emits LEAST" case, with no weakened assertions.
- Consumer-facing risk: a `net11.0` consumer pointed at SQL Server 2019 or a
  database with compat < 160 must explicitly call `UseCompatibilityLevel(150)`.
  Documented in `README.md` / `NUGET_README.md`.

The internal API surface used by the library (`EngineType`,
`SqlServerCompatibilityLevel`, `AzureSqlCompatibilityLevel`,
`AzureSynapseCompatibilityLevel`, `SqlServerDefaultCompatibilityLevel`,
`GetService<IDbContextOptions>()`) is **unchanged** between 10.0.15 and 11.0.0.
No `#if` adapter was needed.

## 2. EF Core 11 breaking changes — impact matrix

| Change | Impact here |
|---|---|
| SQL Server compat default 160 | **Applies** — handled as above |
| `Microsoft.Data.SqlClient` 7.0 (Azure auth deps removed) | None: no Entra auth usage; `SqlConnectionStringBuilder` in the SQL Server fixture still compiles |
| `Migrate()` throws when no migrations | None: no `Migrate()` calls in repo |
| `EFOptimizeContext` removed | None: not used in any project file |
| EF tools packages drop Design dependency | None: no EF tools packages referenced |
| `SqlVector<T>` not auto-loaded | None: no vector properties |
| Primitive collections now require `PrimitiveCollection` | None: no primitive-collection configuration |
| Owned JSON collections without key obsolete | None: no owned/JSON mappings |
| Split queries throw on concurrent modification | None: no `AsSplitQuery()` usage |
| Same owned instance on multiple navigations throws | None: no owned navigations |
| Cosmos changes (unmapped props, sync I/O, `__jObject`, ids, projections, discriminator, truncation, empty collections) | None: no Cosmos provider |
| SQLite drops `netstandard2.0`, targets `net10.0` only | None: targets are `net10.0` / `net11.0`; no direct SQLitePCLRaw bundle references |

## 3. EF Core 11 new features — impact

Mostly additive and in the query/model layer, which this library does not use to
generate its DML — it emits its own SQL from expression trees:

- Complex types + JSON on TPT/TPC, chained `Property` configuration, keys/indexes
  on complex types, complex-type stabilisation — no model in the repo uses them.
- `FullJoin`, `MaxBy`/`MinBy`, `GroupBy` improvements, no-op `CAST` stripping,
  `JsonPathExists`/`JsonContains` — query translation only.
- SQL Server 2025 vector/full-text/hybrid search, JSON indexes, temporal CLR
  period properties — not used.
- `EF.Functions.Least`/`Greatest` exist in **both** EF 10 and 11 (verified in the
  NuGet XML docs); our `SetExpressionTranslator` supports only `Math.Min`/`Max`
  with 2 args on both TFMs. This is a pre-existing limitation, not a regression.
- Migrations CLI improvements (`--add`, `--offline`, config file, wildcards) —
  no migrations in this repo.

## 4. .NET 11 changes — impact

- Nine `Microsoft.Extensions.*` assemblies moved into the shared framework;
  explicit references now warn **NU1510** for `net11.0`-only projects. The docs
  state NU1510 is **not raised for multi-targeted projects**, which is our case
  (`net10.0;net11.0`), and the build produced no NU1510/NU1605/NU1202 warnings.
  The `net11.0` package versions are therefore kept for clarity and to pin the
  EF-aligned stack; removing them is optional and would break `net10.0`.
- Other index entries (ZIP CRC validation, `DateOnly`/`TimeOnly` parsing,
  `Math.Round`, DI scope disposal, `IHost.RunAsync` on BackgroundService
  failure, JIT hardware baseline, etc.) do not touch this library's code paths.

## 5. Provider-specific

- **Npgsql 11.0.0**: DI extension class renamed (binary-only break — samples
  compile); PostgreSQL 16 is now the default target (fixtures/samples use
  `postgres:17`, so fine); transient/concurrency exception wrapping changed —
  `NpgsqlExecutor` catches `PostgresException` by SQL state directly and is
  unaffected; migration rollback and collation changes do not apply.
- **SQLite**: integration tests pass on both TFMs; no direct bundle references.

## 6. Verification performed

- Restore/build: both TFMs, 0 errors, no NU warnings.
- Unit tests: 194 SQL Server + 89 SQLite + 64 Npgsql = 347 per TFM, all pass.
- SQLite integration tests: 46 per TFM, all pass.
- `dotnet pack`: `lib/net10.0` + `lib/net11.0` assets in all four packages;
  nuspec shows EF 10.0.15 vs 11.0.0 per TFM group.
- Not verified: SQL Server/PostgreSQL integration tests (Docker daemon unavailable
  in this environment; the fixtures use `mssql/server:2022-latest` and
  `postgres:17`, both compatible with the new defaults).
