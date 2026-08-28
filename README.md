# NSLabs.EFCore.Extensions

Batched conditional bulk update / upsert for Entity Framework Core — execute *N different* `WHERE` + `SET` operations in **one round-trip, one transaction**, with sequential semantics (later ops see earlier writes).

SQL Server is supported first (batched parameterized script + `MERGE` for upserts). PostgreSQL / MySQL / SQLite providers are planned.

## Why

Standard EF Core:

- `ExecuteUpdateAsync` → 1 filter + 1 payload per call = N round-trips
- `BulkExtensions` / `FlexLabs.Upsert` → PK-only or 1 op per call

This library: `N` heterogeneous `UPDATE` / `UPSERT` / `DELETE` across multiple tables, one `DbCommand`, param-budget chunking (~2100 params on SQL Server), per-op `RowsAffected`.

## Installation

```bash
dotnet add package NSLabs.EFCore.Extensions
dotnet add package NSLabs.EFCore.Extensions.SqlServer
```

Requires `.NET 10` and `Microsoft.EntityFrameworkCore` `10.0.0`.

## Quick start

### Bulk batch (multi-table, single round-trip)

```csharp
var result = await db.BulkExecuteAsync(b =>
{
    b.Update<Item>(op => op.Where(x => x.Id == 6)
                           .Set(x => x.Key1, "Value1")
                           .Set(x => x.Key2, 5));

    b.Update<Order>(op => op.Where(x => x.Status == OrderStatus.Pending)
                            .Set(x => x.Status, OrderStatus.Shipped));

    b.Upsert<Customer>(u => u.On(x => x.Code)
                             .WhenMatched(x => x.Active)
                             .Values(new Customer { Code = "A", Name = "X" }));
});

// per-op counts (SQL Server)
result.Operations[0].RowsAffected
```

### Simple helper when you only use one table

If all your updates are for the same table, you can use this shorter way. It does the same thing, just less code to write:

```csharp
await db.Items.BulkUpdateAsync(b =>
{
    b.Add(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
    b.Add(op => op.Where(x => x.Key1 == "Old").Set(x => x.Key3, 0));
});

// also works with a list of items
await db.Items.BulkUpdateAsync(new[] { e1, e2 });
```

### Deferred builder

```csharp
IBulkBatch batch = db.CreateBulkBatch();
batch.Update<Item>(op => op.Where(x => x.Id == 6).Set(x => x.Key1, "Value1"));
ApplyRules(batch);
var r = await batch.ExecuteAsync(ct);
```

See `docs/DESIGN.md` for full semantics, translation pipeline, and provider strategies.

## Repository

https://github.com/niravinfo/NSLabs.EFCore.Extensions

## License

MIT — see [LICENSE](LICENSE).
