# Compatibility and evidence

The repository targets `net10.0` with `global.json` requesting SDK `10.0.100`, `latestFeature` roll-forward and prereleases disabled. The current resolution is SDK `10.0.401`. Central package versions are Npgsql EF provider `10.0.3`, Npgsql `10.0.3`, and EF Core Relational `10.0.12`. The integration fixture pins `postgres:16-alpine@sha256:33f923b05f64ca54ac4401c01126a6b92afe839a0aa0a52bc5aeb5cc958e5f20`, which runs PostgreSQL `16.14`. These are PoC inputs, not a released compatibility range.

| Library release | EF Core / Npgsql | PostgreSQL | Evidence |
|---|---|---|---|
| None | No supported patch range declared | No supported server range declared | Bounded local PostgreSQL checks cover selected forms; production admission and packed-consumer evidence are not established |

There is one runtime library project and one test project. The tests use Testcontainers to start the configured PostgreSQL 16 image and require Docker or a compatible container runtime. No manually supplied database connection setting is documented by the test harness. The source includes provider-specific query generator and visitor integrations; compatibility beyond the exact restored package versions must be established independently.

Bounded local PostgreSQL checks exercise id-equality root locks, `OrderBy(...).Take(1)`, and synchronous/asynchronous compiled-query forms across carriers A/B/C. Specific tested Q05вЂ“Q12 relational cases reject before issuing a SELECT where required. Arbitrary unmarked-predicate subqueries and global query filters remain uncharacterized. The test inputs include Npgsql SQL target 14, a separate target-15 context smoke check, and a PostgreSQL 16.14 Testcontainers server; none declares a supported compatibility pair. The expected local checks are restore, Release build, full tests, unit-only tests, and pack as documented in [README](../README.md) and [CONTRIBUTING](../CONTRIBUTING.md). The configured Linux workflow covers restore, Release build, full tests and pack; Windows covers restore, Release build and unit tests. Remote CI has not been executed; local preview artifacts exist, with isolated packed-consumer validation still pending. Neither workflow configuration nor compilation alone establishes provider behavior.

Before claiming compatibility, record the exact candidate version set and evidence for each axis: .NET target/SDK, EF Core and Npgsql package versions, PostgreSQL server version, and library API/package version. The lowest supported dependency and any upper bound remain undecided until tests and maintainer review establish them. Do not claim EF Core 8/9/11, PostgreSQL versions other than the tested major, multi-targeting, NativeAOT or trimming support without corresponding evidence.

Npgsql's configured SQL target governs translation. The actual connected server version and installed extensions are separate read-only probe facts. A database probe does not switch the SQL target, certify every replica or role, or install extensions. Capability validation is an explicit caller operation; no host startup/readiness integration is provided.

Package creation and package publication are separate outcomes. A local `.nupkg` does not establish an isolated consumer contract or authorize publication. NuGet package ID availability and ownership are unresolved.

References: [architecture](architecture.md), [capability discovery](capabilities.md), [PoC plan](poc-plan.md).

Exact local research and artifact evidence: [experimental comparison](experiments/query-carriers.md).
