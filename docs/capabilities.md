# PostgreSQL capability probe

`PostgreSqlCapabilityProbe.CheckAsync` is an explicit, read-only probe for one Npgsql-backed `DbContext` and its current database connection. The caller supplies the extension requirements to compare. The probe does not infer application needs, create extensions, choose provider configuration, or manage host startup/readiness. The approved [first-production contract](briefs/production-contract.md) defines the selected target and exclusions.

## API and report

```csharp
Task<PostgreSqlCapabilityReport> PostgreSqlCapabilityProbe.CheckAsync(
    DbContext context,
    IEnumerable<PostgreSqlExtensionRequirement> requirements,
    CancellationToken cancellationToken = default)
```

Each `PostgreSqlExtensionRequirement` has a `Name` and optional exact `RequiredVersion`. Names must be non-empty and unique; invalid input throws `ArgumentException`. Each installed extension is reported as `Name` and `Version`.

The report contains:

- `Outcome`: `Compatible`, `Incompatible`, or `Indeterminate`;
- `SqlTargetVersion`: Npgsql's effective SQL-generation target, including its provider default when no explicit target is configured;
- `ActualServerVersion`: the version reported by the opened `NpgsqlConnection`;
- `InstalledExtensions`: extension names and versions installed in the current database;
- `UnmetRequirements`: caller-declared requirements not observed at the requested exact version;
- optional `FailureType`: the exception type recorded when the probe cannot read facts.

`Compatible` means the configured SQL target does not exceed the observed server version and all supplied extension requirements match. `Incompatible` means the provider or connection is unsupported, the SQL target exceeds the server version, or a supplied extension requirement is unmet. `Indeterminate` means a database/provider operation failed with a database or invalid-operation exception before reliable facts could be established. The outcome describes this probe and its inputs; it is not a package support-range declaration, deployment certification, or host readiness decision.

## Observation and lifecycle

The probe reads the effective SQL target from the Npgsql EF options and the actual version from `NpgsqlConnection.PostgreSqlVersion`. It reads installed extensions from `pg_catalog.pg_extension` (`extname` and `extversion`), then compares only caller-supplied requirements. It does not treat available-but-uninstalled extensions as installed. An installed extension record does not prove that a particular role can call its functions or that an Npgsql plugin is configured.

The method uses the context's current transaction when present and requires its underlying Npgsql transaction to be attached to the observed connection before issuing the catalog query. It opens the EF connection for the probe and pairs that acquisition with a close, preserving a connection that was already open. It creates no transaction and performs no database mutation. Cancellation propagates. Provider/connection mismatches return `Incompatible`; database and invalid-operation failures return `Indeterminate` with a failure type and without exposing exception messages. Other argument errors propagate.

The SQL-generation target and actual server version remain separate facts. The target controls Npgsql SQL generation; the probe does not change it in response to the server version. A successful report covers only the observed connection/database at that time. It does not describe other databases, roles, replicas, or future failover targets.

## Caller responsibility

The caller decides when to invoke the probe, which requirements to declare, and how to handle each outcome. There is no automatic registration hook, startup gate, readiness policy, retry policy, per-query probing, or automatic-target bootstrap in this API. Extension installation and host lifecycle remain outside the library. See the [approved first-production contract](briefs/production-contract.md) for the complete selected scope and exclusions.
