# EntityFrameworkCore.PostgreSQL.Extensions

An experimental PostgreSQL extension library for EF Core and Npgsql. The checkout contains one runtime project and one test project, with prototypes for row-level locking, transaction-scoped advisory locks, atomic insert-ignore, PostgreSQL database time, and a read-only capability probe. Public APIs and supported query shapes remain experimental; no released package or compatibility range has been established.

The repository uses Apache-2.0; see [LICENSE](LICENSE). The upstream repository is [github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions). NuGet package ID ownership and publication are unresolved.

## Current proof status

Bounded local PostgreSQL checks exercise id-equality root locks, `OrderBy(...).Take(1)`, and synchronous and asynchronous compiled-query forms across the three comparison carriers. Specific tested relational cases in Q05–Q12 reject before issuing a SELECT where the shape is unsupported. Arbitrary unmarked-predicate subqueries and global query filters remain uncharacterized and are outside any production support claim. This evidence does not select a production carrier or admit a public API surface. Remote CI has not been executed, and a local package has not yet been built; detailed verification results belong with change evidence.

The query integration code exposes three selectable comparison carriers: `TableAnnotations`, `TranslatorTag`, and `ExpressionTag`. No carrier or public API surface has been selected for production. Current test inputs use .NET SDK 10.0.401, EF Core Relational 10.0.12, Npgsql EF Core 10.0.3 / Npgsql 10.0.3, and a Testcontainers image pinned to PostgreSQL 16.14. Npgsql's configured SQL target is 14; a separate context smoke check configures target 15. These are test inputs, not a supported compatibility pair or range. The integration tests require Docker or a compatible container runtime and do not expose a manually supplied database setting.

The following sketch uses signatures currently present in source. It is experimental and shows only a bounded query form; it does not establish a general provider contract:

```csharp
using EntityFrameworkCore.PostgreSQL.Extensions;

options.UseNpgsql(connectionString,
    npgsql => npgsql.UsePostgreSqlExtensions(PocCarrier.ExpressionTag));

await using var transaction = await db.Database.BeginTransactionAsync(ct);
var row = await db.Records
    .Where(x => x.Id == id)
    .ForUpdate(LockWait.NoWait)
    .SingleAsync(ct);
```

The caller owns the transaction. Unsupported shapes must fail before an unlocked query can succeed.

## Build and run the PoC

Use a stable .NET 10 SDK. The repository's `global.json` requests SDK 10.0.100 with `latestFeature` roll-forward; the current resolution is 10.0.401. Restore NuGet packages and make Docker available for integration tests.

```powershell
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --logger trx
dotnet test -c Release --no-build --filter Category=Unit --logger trx
dotnet pack -c Release --no-build -o artifacts/packages
```

The full test command includes PostgreSQL integration tests. A successful `dotnet pack` would create a local artifact only; it would not establish a supported consumer contract or publish a package.

## Design and implementation notes

- [Architecture](docs/architecture.md) and [internal mechanisms](docs/internal-mechanisms.md) describe the four integration boundaries and their guarantees.
- [Query integration](docs/query-integration.md), [PoC plan](docs/poc-plan.md), and [query gates](docs/query-gates.md) describe the current A/B/C carrier comparison and required proof.
- [Compatibility](docs/compatibility.md) states the verified scope and open evidence gates.
- [Contribution workflow](CONTRIBUTING.md) gives the standalone setup and commands.
