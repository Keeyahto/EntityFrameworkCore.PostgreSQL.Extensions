# Compatibility and evidence

This page separates the selected reference inputs, package dependency metadata and verified consumer support. The implementation targets one `net10.0` runtime assembly. The project is versioned `0.1.0-preview.3`; package ID ownership and NuGet publication remain release decisions. No published package or verified compatibility range is established here.

## Reference inputs

| Axis | Current reference input | What it establishes |
|---|---|---|
| Runtime | `net10.0` | The single target framework in the runtime project |
| SDK request | `10.0.100`, `latestFeature`, prereleases disabled | A roll-forward request in `global.json`; it is not an exact SDK pin for consumers |
| SDK reference resolution | `10.0.401` | The resolved SDK used as the initial reference, not the only SDK admitted by the request |
| EF Core Relational | `10.0.12` | Exact central test/build input and package dependency minimum |
| Npgsql EF Core provider | `10.0.3` | Exact central test/build input and package dependency minimum |
| Npgsql | `10.0.3` | Exact central test/build input and package dependency minimum |
| PostgreSQL server | `16.14`, from `postgres:16-alpine@sha256:33f923b05f64ca54ac4401c01126a6b92afe839a0aa0a52bc5aeb5cc958e5f20` | Pinned reference fixture; it does not imply support for other patches or majors |
| Npgsql SQL-generation target | `14.0` | The reference translation target used against the PostgreSQL 16.14 fixture; SQL target and server version are separate axes |
| Library package metadata | `EntityFrameworkCore.PostgreSQL.Extensions`, `0.1.0-preview.3` | Project identity/version inputs; neither publication nor stable API status follows from them |

The package project declares EF Core Relational dependency range `[10.0.12,11.0.0)` and Npgsql provider and Npgsql ranges `[10.0.3,11.0.0)`. These are the current `.nuspec` dependency intervals, not evidence that every 10.x patch has been verified. Exact package inputs for builds and tests remain the versions above. A consumer's selected package versions and runtime behavior must be assessed against the evidence for that exact set.

## Evidence boundaries

The configured Linux workflow runs restore, Release build, the complete suite, package construction and `scripts/Test-Package.ps1`. The script restores and builds an isolated consumer using the supplied local `.nupkg`, verifies the copied/loaded package bytes, and runs the consumer against real PostgreSQL. The Windows workflow runs restore, Release build and unit tests. Workflow configuration does not establish that any run passed; concrete results belong to change evidence.

Compilation, unit checks, real PostgreSQL execution, package construction, isolated package consumption, compatibility claims and publication are distinct outcomes. A local `.nupkg` does not establish a published package or a supported compatibility range. The consumer runner and full provider suite require Docker or a compatible container runtime.

The Npgsql SQL-generation target controls translation. The connected server version and installed extension versions are separate observed facts. A capability probe reports those facts for one database and the requirements supplied by its caller; it does not switch the SQL target, certify other databases/roles/replicas, install extensions or provide host readiness policy.

## Scope limits

The initial evidence target is the exact input set in the table. Do not infer support for other EF Core/Npgsql package revisions, PostgreSQL server patches or majors, additional TFMs, NativeAOT or trimming from the declared dependency intervals, a successful build, or package creation. Because the query generator integration uses provider-internal APIs, changing EF/Npgsql revisions requires targeted build, provider and packed-consumer evidence. Expanding a support claim requires an explicit support decision and evidence for each newly claimed boundary.

References: [architecture](architecture.md), [capability discovery](capabilities.md), [first-production contract](briefs/production-contract.md), [query integration](query-integration.md).

Exact artifact and execution basis: [local preview verification](experiments/package-verification.md).
