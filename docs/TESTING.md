# Testing Guide — Unit & Integration (Testcontainers)

This guide explains how to run the test suites, how integration tests automatically manage Docker via Testcontainers, and how to troubleshoot.

## 1. Overview

| Suite | Project | What it verifies | Needs Docker | Speed |
|---|---|---|---|---|
| **Unit** | `tests/NSLabs.EFCore.Extensions.Tests.Unit` | SQL generation (golden-SQL), `SqlNode`→`Emit` in `src/NSLabs.EFCore.Extensions.SqlServer/Internal/SqlServerSqlGenerator.cs:358`, param-budget chunking, validation, `SetExpressionTranslator` whitelist | No | ~1s (85 tests) |
| **Integration** | `tests/NSLabs.EFCore.Extensions.Tests.Integration` | Actual `UPDATE`/`MERGE` execution against live SQL Server 2022, `HOLDLOCK`, `TargetAlias=t`, sequential semantics, `ThrowIfZeroAffected`, computed SET (`+`/`COALESCE`/`CASE`/`UPPER` etc.) persistence | **Yes** | ~5-6s + image pull on first run (~1.5min) |

All integration tests are `[SkippableFact]` via `tests/NSLabs.EFCore.Extensions.Tests.Integration/SqlServerTestBase.cs:13` `RequireDatabase()`:

```csharp
if (Fixture.UnavailableReason is { } reason) Skip.If(true, reason);
```

If Docker is unavailable they show as **Skipped!** (`65` skipped) — not failed.

## 2. Prerequisites

*   **.NET 10 SDK** (see `global.json`)
*   **Docker Engine 24+** with daemon running (`docker ps` must work without `sudo`):
    ```bash
    docker --version
    docker ps
    ```
    If you see `permission denied while trying to connect to .../docker.sock`:
    ```bash
    sudo usermod -aG docker $USER   # add yourself to docker group
    newgrp docker                    # apply without logout, or logout/login
    # or for one-off run:
    sg docker -c "dotnet test ..."
    ```
*   Internet access on first run to pull `mcr.microsoft.com/mssql/server:2022-latest` (~1.5GB) and `testcontainers/ryuk:0.14.0`.

No manual container setup — Testcontainers creates ephemeral containers per test run and deletes them afterwards.

## 3. How to Run

### 3.1 Unit tests (no Docker)

All `85` tests in `ComputedSetGoldenSqlTests.cs`, `UpdateGoldenSqlTests.cs`, `ChunkingTests.cs`, etc.

```bash
# All unit
dotnet test tests/NSLabs.EFCore.Extensions.Tests.Unit -c Release

# Only computed SET golden-SQL (32 tests)
dotnet test tests/NSLabs.EFCore.Extensions.Tests.Unit -c Release --filter ComputedSet

# With detailed output
dotnet test tests/NSLabs.EFCore.Extensions.Tests.Unit -c Release --logger "console;verbosity=detailed"

# Watch mode during development
dotnet watch --project tests/NSLabs.EFCore.Extensions.Tests.Unit test
```

Expected:
```
Passed! - Failed: 0, Passed: 85, Skipped: 0, Total: 85
```

### 3.2 Integration tests (requires Docker)

Covers `ComputedSetExecutionTests.cs` (15 v1), `ComputedSetV2ExecutionTests.cs` (16 v2: `+`/`UPPER/LTRIM/SUBSTRING/REPLACE/CONCAT/LEN/ABS/COALESCE/CASE`), `UpdateExecutionTests.cs`, `UpsertExecutionTests.cs`, `TransactionAndSemanticsTests.cs`.

```bash
# Via normal user (after usermod)
dotnet test tests/NSLabs.EFCore.Extensions.Tests.Integration -c Release

# If you just added yourself to docker group and haven't re-logged
sg docker -c "dotnet test tests/NSLabs.EFCore.Extensions.Tests.Integration -c Release"

# Filter examples
sg docker -c "dotnet test tests/NSLabs.EFCore.Extensions.Tests.Integration -c Release --filter ComputedSet"      # 33 tests (v1+v2)
sg docker -c "dotnet test tests/NSLabs.EFCore.Extensions.Tests.Integration -c Release --filter ComputedSetV2"     # 16 v2 only
sg docker -c "dotnet test tests/NSLabs.EFCore.Extensions.Tests.Integration -c Release --filter Update_string_concat_plus_persists"

# Verbose logs (shows Testcontainers startup)
sg docker -c "dotnet test tests/NSLabs.EFCore.Extensions.Tests.Integration -c Release --logger \"console;verbosity=detailed\""
```

Expected with Docker running:
```
Passed! - Failed: 0, Passed: 65, Skipped: 0, Total: 65   // first run ~1.5 min due to image pull, next runs ~5s
```

With Docker stopped/unavailable:
```
Skipped! - Failed: 0, Passed: 0, Skipped: 65, Total: 65
# reason: SQL Server test container is unavailable: ... docker.sock permission denied
```

### 3.3 All suites together

```bash
dotnet test -c Release                          # unit + integration (integration will skip if no Docker)
sg docker -c "dotnet test -c Release"          # unit + integration with container
dotnet test -c Release --filter "ComputedSet" # only computed SET across both suites
```

## 4. How Testcontainers Automatically Handles Docker

File `tests/NSLabs.EFCore.Extensions.Tests.Integration/SqlServerFixture.cs:8`:

```csharp
public sealed class SqlServerFixture : IAsyncLifetime {
  private MsSqlContainer? _container;
  public string? UnavailableReason { get; private set; }
  public async Task InitializeAsync() {
    _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
      .WithEnvironment("ACCEPT_EULA", "Y").Build();
    await _container.StartAsync();                 // ← pulls image if missing, creates container, starts it
    ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString()){ InitialCatalog="NSLabsBulkTests" }.ConnectionString;
    await using var ctx = CreateContext();
    await ctx.Database.EnsureCreatedAsync();       // ← creates tables from TestDbContext model
  }
  public async Task DisposeAsync() {
    if(_container is not null) await _container.DisposeAsync(); // ← deletes container + ryuk sidecar
  }
}
```

Lifecycle (logged as `[testcontainers.org]`):

1.  **Discover** `Docker` via `unix:///var/run/docker.sock` (`Server Version: 29.7.2`).
2.  **Pull** `testcontainers/ryuk:0.14.0` (reaper sidecar) and `mcr.microsoft.com/mssql/server:2022-latest` on first run, cached afterwards (`Docker image ... created`).
3.  **Create & Start** ephemeral `MsSqlContainer` (`Docker container 640ad987958a created` → `Start Docker container`).
4.  **Wait** for readiness by executing `sqlcmd -C -b -r 1 -d master -Q SELECT 1;` until `ready` (`Wait for Docker container to complete readiness checks`).
5.  **Bootstrap** DB: `IntegrationTestDbContext` (`SqlServerFixture.cs:65`) disables identity for `Item.Id`/`AuditLog.Id`/`Pet.PetId` (`ValueGeneratedNever`) for deterministic seeds, then `EnsureCreatedAsync()`.
6.  **Run** each `[SkippableFact]` against `CreateContext()` (`UseSqlServer(ConnectionString)`), sharing the single container via `ICollectionFixture<SqlServerFixture>` (`CollectionDefinition("sqlserver")`).
7.  **Teardown**: `DisposeAsync()` deletes container (`Delete Docker container 640ad987958a`) even if tests crash — Ryuk guarantees cleanup.

No manual `docker run`/`docker rm`/`sqlcmd` needed. Tests run in-process with normal `dotnet test`; Testcontainers handles port mapping, `SA` password, and `ConnectionString`.

## 5. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `permission denied while trying to connect to the docker API at unix:///var/run/docker.sock` | User not in `docker` group | `sudo usermod -aG docker $USER && newgrp docker` or `sg docker -c "dotnet test ..."` |
| `Docker daemon not running` / `Cannot connect to the Docker daemon` | Docker service stopped | `sudo systemctl start docker` (Linux) / start Docker Desktop (Win/Mac) |
| First run slow / `Docker image mcr.microsoft.com/mssql/server:2022-latest created` takes 60s | Image pull | Wait once; cached afterwards |
| `Skipped! 65` but Docker is running | Stale shell groups | Re-login or use `sg docker` |
| `Port already allocated` | Stale container | `docker ps -a` → `docker rm -f $(docker ps -aq)` or Ryuk will clean on next run |
| Tests fail with `Login failed for user 'sa'` | Container not ready yet | Testcontainers already waits via `sqlcmd SELECT 1`; retry `dotnet test` |

## 6. CI Usage

In GitHub Actions (or any CI with Docker):

```yaml
- uses: actions/setup-dotnet@v6
  with: { dotnet-version: '10.0.x' }
- name: Run unit
  run: dotnet test tests/NSLabs.EFCore.Extensions.Tests.Unit -c Release
- name: Run integration
  run: dotnet test tests/NSLabs.EFCore.Extensions.Tests.Integration -c Release
# service `docker` is available by default on ubuntu-latest runners
```

No extra `docker pull` step — `MsSqlBuilder` pulls automatically.

## 7. Adding New Tests

*   **Golden-SQL**: add to `tests/NSLabs.EFCore.Extensions.Tests.Unit/ComputedSetGoldenSqlTests.cs` via `Harness.GenerateSingle(b => b.Update<...>(op => op.Set(...)))` and `Assert.Contains("UPPER([Col])", sql)`.
*   **Integration**: add to `tests/NSLabs.EFCore.Extensions.Tests.Integration/ComputedSetV2ExecutionTests.cs` as `public async Task ...` with `[SkippableFact]`, `RequireDatabase()`, seed via `Fixture.CreateContext()` + `SaveChangesAsync()`, then `BulkExecuteAsync` + `Assert.Equal(expected, (await verify.Items.AsNoTracking()...).KeyX)`. See existing `Update_string_concat_plus_persists:1` for template.

See `docs/COMPUTED_SET_SUPPORT.md` for whitelisted operators/functions coverage.
