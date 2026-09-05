# Proposal: Redesign the Upsert Public API (builder surface only)

Status: Implemented — `MatchOn` / `Update` / `UpdateWhen` (optional) / `Insert` shipped across `src`, `tests`, `samples`, `README`/`NUGET_README`/`DESIGN`. Verified: full build 0 errors; unit suites 107 SqlServer + 38 Sqlite + 39 Npgsql (golden SQL byte-identical → engine untouched); live integration 28 Sqlite + 73 SqlServer + 15 Npgsql.
Freedom: library has no users yet, 1.0.0 unreleased → clean break allowed, no `[Obsolete]` aliases needed.

---

## 1. Problem (why the current API confuses)

Four defects, all in the builder surface — the engine underneath is sound and stays untouched:

| # | Defect | Evidence |
|---|--------|----------|
| P1 | `WhenMatched(bool)` steals FlexLabs' name with the opposite meaning. In FlexLabs `WhenMatched` = *what to write when matched* (update shape). In ours it = *only update if this boolean holds* (guard). Anyone arriving from FlexLabs misreads our chain every time. | FlexLabs README + wiki `Usage` (verified 2026-09-04): `.Upsert(row).On(key).WhenMatched(updateShape).RunAsync()`; no boolean-guard overload exists there |
| P2 | `Set` vs `Values` never says which path each serves. Reader can't tell matched-write apart from insert-row. | `README.md` quick-start before this proposal |
| P3 | Lookup-key values hide inside the `Values` row. `Code = "A"` is simultaneously the insert value *and* the conflict lookup — implicit, undocumented. | `BulkBatch.cs:374-382`: `KeyValues` sliced from `InsertValues` prefix |
| P4 | Update-vs-insert value split is undiscoverable. Nothing states the rule "no `Set` → matched row gets the `Values` row; with `Set` → matched row gets only the `Set` payload". | `BulkBatch.cs:317-336` implements it; no doc states it |

---

## 2. Ground truth: engine semantics (verified, do not change)

Read from `BulkBatch.BindUpsert` (`BulkBatch.cs:240-386`), confirmed by live suites (13/13 Npgsql, SQLite, SQL Server):

1. **Conflict target** (`On(...)`, `BulkBatch.cs:245-249`): columns defining "same row". Omitted → entity PK. Accepts only `x => x.Prop` / `x => new { x.A, x.B }` (`BulkBatch.cs:450-474`, else throws). Becomes the SQL arbiter: `ON CONFLICT ("Code")`, `MERGE ... ON t.Code = s.Code`.
2. **Must map to a real UNIQUE/PK** on PG + SQLite (DB rule, not ours): missing → `42P10`/`23505` (PG) / error 19 (SQLite), both rethrown with the "add a UNIQUE index" hint. SQL Server `MERGE` needs no constraint but errors on double-match — hence client-side dup-key pre-validation (`BulkBatch.cs:100-141`).
3. **Conflict columns can't be written** (`BulkBatch.cs:290-294` throws) and always lead the insert column list (`BulkBatch.cs:261-266`).
4. **Update payload rule** (`BulkBatch.cs:317-336`): explicit `Set(...)` calls (const or computed) form the matched-write payload; otherwise the full `Values` row minus key/generated columns is written back (`"c" = excluded."c"`). Guard without payload throws (`BulkBatch.cs:347-356`).
5. **Guard** translates via the normal predicate translator (`BulkBatch.cs:355`) and emits as `DO UPDATE ... WHERE <guard>` with target-qualified columns on PG (`"Table"."Col"`, D8 of `docs/PLAN-postgresql.md`).
6. **Explicit computed `Set` references the *target* row** (`EmitQualified`); there is no `EXCLUDED`-referencing expression node. Referencing proposed insert values inside an explicit `Set` is unsupported (the no-`Set` default path covers it via `excluded.`). Document as a known gap, not a bug.

FlexLabs parity check (wiki `Usage`, verified): their `Upsert(entity)` ≈ our `Values(row)`; their `.On()` ≈ our `.On()` incl. PK default; their `.WhenMatched(shape)` ≈ our `Set(...)` (theirs whole-shape, ours per-column — strictly more expressive for partial writes, plus computed expressions like `v.Visits + 1` ≡ our `x => x.Amount * 2`); their two-arg `(db, ins)` shape has no equivalent in ours (see §2.6). They have **no guard slot** — our guard is extra power with a stolen name (P1).

---

## 3. Proposal: rename full builder surface, reorder the story, document the rules

### 3.1 The change (builder surface only)

Running example for this section (used for both scripts below): nightly supplier-feed sync over the sample model (`Samples.Shared` `Product`: UNIQUE `Sku`, `Price`, `StockQuantity`, `IsActive`). New SKUs get inserted; existing ones get price/stock refresh — except discontinued products, which humans deliberately deactivated and the feed must not resurrect. That exception is the guard's entire reason to exist: without it, every re-sync would overwrite rows people intentionally changed.

```csharp
// AFTER (approved)
b.Upsert<Product>(u => u.MatchOn(p => p.Sku)              // 1. identity: UNIQUE Sku defines "same row" (omit → PK)
                         // .MatchOn(p => new { p.Sku, p.Category }) // composite form
                         .Update(p => p.Price, feed.Price) // 2. matched: refresh price + stock only (one call per column, chain for many)
                         .Update(p => p.StockQuantity, feed.Stock)
                         .UpdateWhen(p => p.IsActive)     // 3. optional guard: hands off discontinued rows (omit → always update on match)
                         .Insert(feedRow));               // 4. not matched: insert the whole row (Name, Category, ...); Insert(rows) for many
```

Naming decisions (approved):

| Slot | Now (`BulkOperationBuilders.cs`) | Approved | Why |
|------|----------------------------------|----------|-----|
| Conflict target | `On` | **rename → `MatchOn`** | Says what it does (how to find existing row). Selector-only, FlexLabs-style: `p => p.Sku` single, `p => new { ... }` composite. Key value is NOT passed separately — it is sliced from the `Insert` row prefix (`BulkBatch.cs:261-266, 374-379`), matching native `MERGE ON t.C = s.C` / `ON CONFLICT (C)` semantics on all three providers (verified: no provider supports a compare value distinct from the insert row) |
| Matched writes | `Set` | **rename → `Update`** | Per-column const/computed, one call per column (chaining = many columns). Whole-object shapes rejected deliberately: a runtime object can't distinguish unset vs intentional `0`/`null`; per-column selectors explicitly mark dirty columns |
| Guard | `WhenMatched` | **rename → `UpdateWhen`** | Fixes the FlexLabs collision (P1): theirs = update shape, ours = boolean filter. Reads as "update only when…", maps to `WHEN MATCHED AND` (SQL Server) / `DO UPDATE ... WHERE` (PG/SQLite). Optional |
| Insert rows | `Values` | **rename → `Insert`** | `Values` never signalled "insert if not found". `Insert(row)` / `Insert(rows)` names the `WHEN NOT MATCHED THEN INSERT` branch |

`DbSet.BulkUpsertAsync(b => b.Add(u => ...))` table API is unaffected (forwards to the same builder).

### 3.1.1 Real generated SQL for the example above (feed: SKU-1 / Widget / 12.99 / 150)

Produced by the actual generators (scratch harness over the sample `Product` mapping, since removed) — not hand-written:

**SQL Server (`MERGE`):**

```sql
MERGE INTO [Products] WITH (HOLDLOCK) AS [t]
USING (VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6)) AS [s]([Sku], [Category], [IsActive], [LastRestocked], [Name], [Price], [StockQuantity])
ON [t].[Sku] = [s].[Sku]
WHEN MATCHED AND [t].[IsActive] = 1 THEN UPDATE SET [Price] = @p7, [StockQuantity] = @p8
WHEN NOT MATCHED THEN INSERT ([Sku], [Category], [IsActive], [LastRestocked], [Name], [Price], [StockQuantity])
VALUES ([s].[Sku], [s].[Category], [s].[IsActive], [s].[LastRestocked], [s].[Name], [s].[Price], [s].[StockQuantity]);
-- @p0..@p6 = insert row (SKU-1, Tools, True, 2026-09-01, Widget, 12.99, 150); @p7 = 12.99, @p8 = 150 (matched-write payload)
```

**SQLite (`ON CONFLICT`, PostgreSQL identical modulo `TRUE` + qualified guard):**

```sql
INSERT INTO "Products" ("Sku", "Category", "IsActive", "LastRestocked", "Name", "Price", "StockQuantity")
VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6)
ON CONFLICT ("Sku") DO UPDATE SET "Price" = @p7, "StockQuantity" = @p8 WHERE "IsActive" = 1;
-- same params as above. PG emits WHERE "Products"."IsActive" = TRUE instead.
```

Reading the scripts proves the four slots: `MatchOn` → the `ON`/`ON CONFLICT` arbiter; `Update` → the matched-write list (`@p7/@p8`, deliberately *different* params from the insert row); `UpdateWhen` → the `AND`/`WHERE` gate; `Insert` → the `VALUES` row + `WHEN NOT MATCHED ... INSERT` branch.

### 3.2 What is NOT changing

- Engine: `BulkBatch.BindUpsert`, all three generators, all three executors — **zero edits**. Proof: existing golden-SQL suites must pass byte-identical after the rename (only call sites change).
- `MatchOn` semantics incl. PK default and composite `MatchOn(x => new { ... })`; key value always comes from the `Insert` row (no separate compare value on any provider).
- Guard-without-payload error, conflict-col-write error, dup-key validation, zero-row no-op chunk.
- `Update`-builder `Set`/`SetProperty` untouched. Upsert-builder `Set`/`SetProperty` → `Update` (alias shape decided at implementation; default is `Update` + `SetProperty` kept for EF parity unless it confuses).

### 3.3 README contract to write (the rules, stated plainly)

1. `Insert` rows carry **both** the lookup key and the insert payload; `MatchOn` names which columns of the row form the lookup (never a separate value).
2. No `Update` → match rewrites the row from `Insert` (minus keys). With `Update` → match writes only `Update` (may differ entirely from insert).
3. `UpdateWhen` is optional and filters *whether* a found row updates; `false` → row untouched, counts 0 for that row. It never blocks the insert branch.
4. Conflict columns need a real UNIQUE/PK on PG + SQLite; `Update` can't target them.
5. Computed `Update` sees the *target* row; `excluded.`-style refs unsupported explicitly (default path uses them implicitly).

---

## 4. Touchpoint inventory (rename `On` → `MatchOn`, `Set` → `Update`, `WhenMatched` → `UpdateWhen`, `Values` → `Insert`)

All four renames are mechanical (same overload shapes: `MatchOn` selector-only single/composite; `Update` per-column const + computed; `UpdateWhen` single predicate; `Insert` single row + `IEnumerable<T>`). No test-logic edits.

- `src/`: declarations in `BulkOperationBuilders.cs` (`On:59`, `WhenMatched:66`, `Set:73,80`, `SetProperty:88-89`, `Values:91,98`) + 1 use (`BulkBatch.cs:349-355` reads `builder.Guard`; error text at `:352` mentions `WhenMatched(...)` — update to `UpdateWhen(...)`)
- `tests/`: `Values` sites ~80 (every upsert test); `WhenMatched` sites ~15 — `Unit.SqlServer` (`UpsertGoldenSqlTests:78`, `ComputedSetGoldenSqlTests:260`, `UpsertExecutionTests:152,156`, `ComputedSetExecutionTests:336,356`), `Unit.Sqlite` (`SqliteUpsertGoldenSqlTests:28`), `Unit.Npgsql` (`NpgsqlUpsertGoldenSqlTests:28`), `Integration.Npgsql` (`NpgsqlUpsertExecutionTests:55`); plus `On`/`Set` sites in the same files
- `samples/`: 0 uses of `WhenMatched` (only `On`+`Values`) — rename to `MatchOn`+`Insert`
- Docs: `README.md` quick-start + §3 story example, `NUGET_README.md` if mirrored, `docs/DESIGN.md:156-168` upsert section (also still shows stale `.InsertValues(...)` name — fix to `.Insert(...)` in passing)

---

## 5. Rollout plan (naming approved — implement)

1. Rename in `BulkOperationBuilders.cs`: `On` → `MatchOn`, `Set` → `Update`, `WhenMatched` → `UpdateWhen`, `Values` → `Insert` (+ decide `SetProperty` alias); update `BulkBatch.cs:352` error text to `UpdateWhen(...)`.
2. Mechanical rename at all call sites (§4); no test-logic edits.
3. Rewrite `README.md` upsert example in story order (§3.1 AFTER) + add §3.3 contract block; mirror to `NUGET_README.md`; fix `DESIGN.md` upsert API block (incl. stale `InsertValues`).
4. Verify: full build + all unit suites (golden SQL byte-identical proves engine untouched) + SQLite integration (no-Docker) + Npgsql/SQL Server integration where Docker available.
5. Update this doc status → Implemented.

---

## 6. Open decisions (resolved, pending D3)

- D1: conflict target — decided `MatchOn` (selector-only, single/composite; value from `Insert` row).
- D2: matched writes — decided `Update` (per-column, chain for many; whole-object shapes rejected).
- D3: guard name — decided `UpdateWhen` (optional; `WHEN MATCHED AND` / `WHERE`).
- D4: insert name — decided `Insert` (`row` + `IEnumerable<T>`; `WHEN NOT MATCHED THEN INSERT` branch).
- D5: docs-only for §3.3, or also XML-doc the four slots on the builder methods themselves? (Recommend: both — XML docs are cheap and travel with IntelliSense.)
