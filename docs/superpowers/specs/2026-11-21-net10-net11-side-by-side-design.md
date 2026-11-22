# Side-by-side EF Core 10 / 11 support — design

**Intent:** keep the shipped `net10.0` package on EF Core 10.x while adding a
`net11.0` build on EF Core 11.x, so consumers of either .NET version get a
matching EF provider stack. No direct jump to .NET 11 for existing consumers.

**Approaches considered:**
1. Multi-target the whole repo with conditional central package versions
   (chosen) — one codebase, two builds, NuGet picks the right assets.
2. Separate `net11` branch/project copied from `net10` — duplicates code and
   fixes, rejected.
3. Only bump EF 11 and raise `TargetFramework` to `net11.0` — breaks existing
   `net10.0` consumers, rejected.

**Design:**
- `Directory.Build.props`: `net10.0;net11.0`.
- `Directory.Packages.props`: shared third-party versions in one group;
  `net10.0` group pins EF 10.0.15 / Npgsql 10.0.4 / Extensions 10.0.15;
  `net11.0` group pins EF 11.0.0 / Npgsql 11.0.0 / Extensions 11.0.0.
- Provider internals (`SqlServerOptionsExtension`, EF1001 APIs) are isolated in
  the provider projects. Their API surface is identical in EF 10.0.15 and
  11.0.0 — no `#if NET11_0` adapter was needed. The only difference that reaches
  the library is the default compat level (150 → 160), which is inherited from
  EF rather than hardcoded. See `docs/ef-core-11-upgrade-analysis.md`.
- Unit tests run on both TFMs; the one golden-SQL test whose premise depends on
  the default compat level is TFM-specific (throws on `net10.0`, emits `LEAST`
  on `net11.0`).
- NuGet package ships `lib/net10.0` and `lib/net11.0` assets.

**Non-goals:** changing the public API, dropping `net10.0`, adding EF 9 or
older support, integration-test CI on both TFMs (Docker-dependent).
