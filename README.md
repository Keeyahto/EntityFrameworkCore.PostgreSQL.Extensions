# EntityFrameworkCore.PostgreSQL.Extensions

[Русский](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/README.ru.md)

Typed PostgreSQL operations for Entity Framework Core and Npgsql:

- **Locking:** row locks in LINQ queries and transaction-scoped advisory locks.
- **Data and transactions:** atomic single-row insert-ignore, PostgreSQL wall-clock access and read-only transactions.
- **Configuration and diagnostics:** an explicit probe of the configured SQL target, server version and required database extensions.

One `net10.0` package, one namespace: `EntityFrameworkCore.PostgreSQL.Extensions`. The current version is **0.1.0-preview.6**. Public NuGet availability is not established; [getting started](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/docs/getting-started.md) explains local package installation. Preview APIs may change.

## Example

With a mapped `Records` entity set and an existing row, register row-lock support and perform the work in your own transaction:

```csharp
using EntityFrameworkCore.PostgreSQL.Extensions;
using Microsoft.EntityFrameworkCore;

options.UseNpgsql(connectionString,
    npgsql => npgsql.UsePostgreSqlExtensions());

await using var transaction = await db.Database.BeginTransactionAsync(ct);
var record = await db.Records
    .Where(x => x.Id == id)
    .ForUpdate(LockWait.NoWait)
    .SingleAsync(ct);

record.Payload = "updated";
await db.SaveChangesAsync(ct);
await transaction.CommitAsync(ct);
```

The row remains locked until the transaction completes. Unsupported marked queries fail explicitly. Row locking supports a bounded root-entity query grammar; joins, projections, includes and global filters are excluded. See [Locking](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/docs/query-integration.md) for the complete scope and PostgreSQL concurrency behavior.

The caller owns transactions, cancellation and retry policy. The other operation families can be used independently of row-lock registration.

## Documentation

Read the [English documentation](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/docs/index.md) or [документацию на русском](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/docs/ru/index.md). Guides group related operations and include examples, constraints and troubleshooting.

The reference combination is EF Core Relational `10.0.12`, Npgsql/provider `10.0.3` and PostgreSQL `16.14`, with Npgsql SQL target `14.0`. See [Compatibility](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/docs/compatibility.md) before choosing other versions.

For builds, tests and the local documentation website, see [Contributing](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/CONTRIBUTING.md). Licensed under [Apache-2.0](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/LICENSE).
