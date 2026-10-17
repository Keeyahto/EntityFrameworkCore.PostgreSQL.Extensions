# Contributing

This is a standalone .NET 10 repository with one runtime project and one test project. `global.json` requests SDK `10.0.100`, allows `latestFeature` roll-forward and disables prereleases; the reference resolution is SDK `10.0.401`. Restore requires NuGet access. Full PostgreSQL integration and the packed-consumer runner require Docker or a compatible container runtime.

From the repository root:

```powershell
dotnet restore
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release --logger trx
dotnet test --no-build --configuration Release --filter Category=Unit --logger trx
dotnet pack src/EntityFrameworkCore.PostgreSQL.Extensions --no-build --configuration Release --output artifacts/packages
pwsh ./scripts/Test-Package.ps1 -PackagePath ./artifacts/packages/EntityFrameworkCore.PostgreSQL.Extensions.0.1.0-preview.2.nupkg
```

The integration fixture uses the pinned PostgreSQL 16.14 Testcontainers image. The package runner inspects the package, copies its exact bytes into a fresh local feed and package cache, restores and builds the tracked consumer template in a temporary workspace outside the checkout, then runs it against real PostgreSQL. The consumer has a `PackageReference` and no project or source reference. An evidence directory may be supplied with `-EvidenceDirectory`; `-KeepWorkspace` retains the temporary workspace for diagnosis.

GitHub Actions configures a Linux restore, Release build, full test run, package build and isolated consumer run. Its Windows job configures restore, Release build and tests filtered to `Category=Unit`. These are configured workflows; their presence does not report a run outcome.

Read [architecture](docs/architecture.md), [query integration](docs/query-integration.md), [compatibility](docs/compatibility.md) and the [first-production contract](docs/briefs/production-contract.md) before changing their mechanisms or claims. Preserve the four independent integration boundaries, fail-closed row-lock query behavior, caller-owned transactions, cancellation propagation and native PostgreSQL errors. Keep provider evidence distinct from compilation and package construction, and keep all actual outcomes with their change evidence.

The implementation targets the selected parameterless `UsePostgreSqlExtensions()` registration and expression-interceptor row-lock path. Comparison carriers are research history, not production runtime options. Do not add query shapes, dependency support claims, API compatibility promises or publication behavior beyond the current contract and evidence.
