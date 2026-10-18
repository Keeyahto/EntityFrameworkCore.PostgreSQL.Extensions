# Configuration and diagnostics

Configure each `DbContext` with the Npgsql EF Core provider. The library uses one namespace, `EntityFrameworkCore.PostgreSQL.Extensions`. This guide explains registration, database requirement checks and common failure causes.

<a id="registration-api"></a>
## Register the operations you use

For row-lock queries:

```csharp
options.UseNpgsql(connectionString,
    npgsql => npgsql.UsePostgreSqlExtensions());
```

`UsePostgreSqlExtensions(this NpgsqlDbContextOptionsBuilder)` returns the same builder. Registration is parameterless and idempotent. Use it in the Npgsql options callback for `AddDbContext`, `AddDbContextFactory` or pooled-context registration as appropriate to your application. Options and query state remain isolated across contexts and pools.

The row-lock integration keeps Npgsql's native translator and replaces the query SQL-generator factory. Competing generator or translator replacements before or after registration are unsupported and fail explicitly. Manually composed `UseInternalServiceProvider` setups are unsupported. Additive plugins such as Npgsql NodaTime can coexist in either registration order; see the reference versions in [Compatibility](compatibility.md).

Advisory locks, insert-ignore, the typed clock read, read-only command and capability probe need no row-lock registration. Clock expressions separately require `modelBuilder.MapClockTimestamp()` in `OnModelCreating`; see [Data and transactions](internal-mechanisms.md#clock-mapping-api).

<a id="target-api"></a>
## SQL target and connected server

Npgsql's effective SQL-generation target controls translation. The server version observed through the connection is a separate fact. You may select the target through the native Npgsql configuration; the reference examples use `SetPostgresVersion(14, 0)` against PostgreSQL `16.14`.

The library does not automatically change the SQL target when it connects, install extensions or probe on each query. An application decides which databases, roles and failover targets need checking and when it is safe to begin work.

<a id="probe-api"></a>
## Inspect database requirements

```csharp
var report = await PostgreSqlCapabilityProbe.CheckAsync(
    db, Array.Empty<PostgreSqlExtensionRequirement>(), ct);
Console.WriteLine(report.Outcome);
```

An empty requirements list checks the provider/target/server relationship and reports the installed extension inventory. If your application requires an extension, supply it explicitly:

```csharp
var requirements = new[]
{
    new PostgreSqlExtensionRequirement("pg_trgm")
};
var report = await PostgreSqlCapabilityProbe.CheckAsync(db, requirements, ct);
```

`RequiredVersion` is an optional exact version string, for example `new PostgreSqlExtensionRequirement("pg_trgm", "1.6")`. This is a requirement supplied by the caller, with no claim that it is installed. Names must be non-empty and unique; invalid requirements throw `ArgumentException`.

<a id="outcomes-api"></a>
### Interpret the report

| Outcome | Meaning |
|---|---|
| `Compatible` | Effective SQL target does not exceed the observed server version, and every supplied extension requirement matches |
| `Incompatible` | Unsupported provider/connection, SQL target above the server version, or an unmet extension requirement |
| `Indeterminate` | A database/provider operation failed with a database or invalid-operation exception before reliable facts were established |

The report contains `Outcome`, `SqlTargetVersion`, `ActualServerVersion`, `InstalledExtensions`, `UnmetRequirements` and optional `FailureType`. An installed extension has `Name` and `Version`; a requirement has `Name` and optional exact `RequiredVersion`. Failure details expose an exception type, without the exception message. Cancellation propagates, and other argument errors propagate.

The read-only probe reads `pg_catalog.pg_extension`. Only installed extensions count; available but uninstalled extensions do not. An inventory record does not prove the current role can call every extension function or that the corresponding Npgsql plugin is configured. A successful report describes one database connection at that time and does not certify all query capabilities, other roles/replicas or future failover targets.

The probe borrows a correctly attached current transaction when present, and balances its EF connection open/close acquisition. It creates no transaction or database objects. You decide when to call it, which requirements to declare and what each outcome means for application readiness. `Indeterminate` does not establish readiness.

<a id="troubleshooting-api"></a>
## Troubleshooting

| Symptom | Check |
|---|---|
| A marked query fails translation or reports missing registration | Add `UsePostgreSqlExtensions()` to the options used by that exact context |
| A query is rejected before SELECT | Compare the complete expression and model with the [supported grammar](query-integration.md#query-shapes-api); inspect global filters and eager navigations |
| A lock/read-only command reports no current transaction | Begin an explicit EF transaction and execute on its context/connection |
| A service-composition error occurs | Remove conflicting generator/translator replacements and manual internal service-provider composition |
| `55P03` with `NoWait` | Another transaction holds an incompatible lock; choose the application's response deliberately |
| A wait is canceled or PostgreSQL aborts a transaction | Handle the native error and complete/replace the transaction according to application policy |
| Insert-ignore rejects a selector or mapping | Select a mapped unfiltered unique key and check [mapping/default limits](internal-mechanisms.md#mapping-api) |
| The probe is `Incompatible` | Inspect target/server versions and `UnmetRequirements` |
| The probe is `Indeterminate` | Inspect `FailureType`, database availability and connection/transaction state; redact secrets in diagnostics |
| Calling `ClockTimestamp()` throws in client code | Use the mapped function inside an EF query or `ReadClockTimestampAsync` for a direct value |

Do not turn a rejected locking query into an unlocked fallback. Native PostgreSQL errors may be wrapped by EF/Npgsql; inspect the native inner exception and SQLSTATE when appropriate. The library adds no retry or uncertain-commit classification.
