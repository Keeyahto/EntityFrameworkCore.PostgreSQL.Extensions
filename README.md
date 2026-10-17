# EntityFrameworkCore.PostgreSQL.Extensions

A preview PostgreSQL extension package for EF Core and Npgsql. The current implementation provides typed row locking, transaction-scoped advisory locks, atomic single-row insert-ignore, PostgreSQL clock access and mapping, a read-only transaction command, and a caller-invoked capability probe. It is one `net10.0` runtime assembly in the `EntityFrameworkCore.PostgreSQL.Extensions` namespace.

The project declares package ID `EntityFrameworkCore.PostgreSQL.Extensions` and version `0.1.0-preview.2`. NuGet publication and package-ID ownership are unresolved; this checkout does not establish a published package or a verified consumer compatibility range. The repository uses Apache-2.0; see [LICENSE](LICENSE).

## Row locking

Row-lock queries use the parameterless provider registration. The caller starts and owns the explicit transaction:

```csharp
using EntityFrameworkCore.PostgreSQL.Extensions;

options.UseNpgsql(connectionString,
    npgsql => npgsql.UsePostgreSqlExtensions());

await using var transaction = await db.Database.BeginTransactionAsync(ct);
var row = await db.Records
    .Where(x => x.Id == id)
    .ForUpdate(LockWait.NoWait)
    .SingleAsync(ct);
```

The query integration validates the original query and model, carries immutable lock intent through an internal tag, and adds `FOR ... OF` structurally to the final SELECT. It retains Npgsql's native translator and replaces the query SQL-generator factory. A command interceptor checks that the generated request was fulfilled by the expected services and executes on the current EF transaction; it never rewrites command text. Registration is idempotent. Manually configured EF internal service providers and competing query-generator or translator replacements are unsupported.

The initial row-lock grammar is deliberately closed: one directly mapped root table, entity materialization, direct mapped scalar equality predicates combined with `AND`, supported entity terminals or enumeration, and an optional direct-column ordering chain followed by `Take(1)`. Lock strength and wait mode are compile-time constants. Joins, projections, includes, global filters, navigation or subquery predicates, offsets, arbitrary limits, aggregates, set operations, and other unknown shapes are rejected before issuing the requested SELECT. The implementation and exact boundaries are described in the [first-production contract](docs/briefs/production-contract.md) and [query integration](docs/query-integration.md). PostgreSQL ordering, `SKIP LOCKED`, transaction, cache and mapping limits still apply; the contract does not promise a universal lock order or fairness.

## Other APIs

- `AcquireTransactionAdvisoryLockAsync` accepts a text, `long`, or two-`int` key and uses PostgreSQL transaction-scoped advisory locks under the caller's current transaction. Text keys use PostgreSQL `hashtextextended`; the library does not use a .NET hash.
- `InsertIgnoreAsync` inserts one mapped entity and ignores only conflicts on its selected mapped, unfiltered unique key. It does not attach or mutate the entity, call `SaveChanges`, return generated values, or create a transaction.
- `DatabaseClock.ClockTimestamp()` maps to `pg_catalog.clock_timestamp()` through `MapClockTimestamp()`. `Database.ReadClockTimestampAsync()` reads one UTC server value and balances connection ownership acquired by that call.
- `SetTransactionReadOnlyAsync()` sends PostgreSQL's native command in the caller's current transaction. `PostgreSqlCapabilityProbe.CheckAsync()` reports the configured SQL target, observed server version and declared extension requirements for one database.

These APIs do not create, commit, retry, or otherwise own application transactions. Cancellation and PostgreSQL errors propagate. The capability probe is caller-invoked; it does not install extensions, change the SQL target, or manage host readiness. See [architecture](docs/architecture.md) for the shared boundaries and feature limits.

## Compatibility and verification

The implementation targets `net10.0`. The repository requests SDK `10.0.100` with `latestFeature` roll-forward; the reference resolution is `10.0.401`. The exact package inputs are EF Core Relational `10.0.12`, Npgsql EF Core `10.0.3`, Npgsql `10.0.3`, and PostgreSQL `16.14` from the pinned Testcontainers image. The reference Npgsql SQL-generation target is `14.0`. The SDK request is not an exact consumer SDK pin, and these test inputs do not certify all package revisions admitted by the declared dependency intervals.

The project metadata declares dependency intervals starting at EF Core Relational `10.0.12` and Npgsql packages `10.0.3`, with an upper bound below major 11. Those package ranges are not a verified compatibility matrix. Provider, packed-consumer and release claims require their corresponding current evidence; `dotnet pack` alone only constructs a local artifact. See [compatibility](docs/compatibility.md).

The configured Linux workflow restores, builds, runs the full suite, packs the library and invokes the isolated packed-consumer runner. The Windows workflow runs unit tests after restore and build. Workflow configuration describes checks, not their results. Full PostgreSQL checks and packed-consumer execution require Docker or a compatible container runtime.

## Build and verify locally

From the repository root, with a .NET 10 SDK and Docker available:

```powershell
dotnet restore
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release --logger trx
dotnet pack src/EntityFrameworkCore.PostgreSQL.Extensions --no-build --configuration Release --output artifacts/packages
pwsh ./scripts/Test-Package.ps1 -PackagePath ./artifacts/packages/EntityFrameworkCore.PostgreSQL.Extensions.0.1.0-preview.2.nupkg
```

The package runner copies the supplied package into a fresh isolated local feed, restores and builds a consumer without a project/source reference, then runs that consumer against real PostgreSQL. Keep actual command outcomes with the change evidence; they are not inferred from this documentation or from configured CI.

## Project references

- [Architecture](docs/architecture.md) describes the four integration mechanisms and their boundaries.
- [Query integration](docs/query-integration.md) and the [first-production contract](docs/briefs/production-contract.md) define row-lock behavior and its finite grammar.
- [Compatibility](docs/compatibility.md) records version inputs and the limits of current claims.
- [Contributing](CONTRIBUTING.md) describes the repository workflow.
- [Experimental carrier research](docs/experiments/query-carriers.md) retains comparison evidence; it does not supersede the selected implementation contract.
