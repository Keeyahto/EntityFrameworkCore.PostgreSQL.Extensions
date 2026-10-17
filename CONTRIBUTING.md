# Contributing

This is an experimental, independently buildable .NET 10 repository. It contains one runtime project and one test project. Use a stable .NET 10 SDK; `global.json` requests 10.0.100 and allows the latest feature band. The current machine resolves that request to SDK 10.0.401. NuGet restore and Docker or a compatible container runtime are required for the full PostgreSQL test suite.

From the repository root:

```powershell
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --logger trx
dotnet test -c Release --no-build --filter Category=Unit --logger trx
dotnet pack -c Release --no-build -o artifacts/packages
```

The integration fixture starts an isolated PostgreSQL 16 Alpine container through Testcontainers. No test setting for a manually supplied PostgreSQL database is defined. The Linux workflow runs restore, Release build, the full test suite and pack. The Windows workflow runs restore, Release build and tests filtered to `Category=Unit`. These workflows describe configured checks; their presence does not establish that a run passed.

Read [architecture](docs/architecture.md), [query integration](docs/query-integration.md), [PoC plan](docs/poc-plan.md), and [compatibility](docs/compatibility.md) before changing those mechanisms or claims. Prefer existing EF Core/Npgsql behavior where it provides the required semantics. Preserve fail-closed query handling, caller-owned transaction boundaries, cancellation propagation, and PostgreSQL SQLSTATE behavior.

Implement the selected expression-interceptor design and finite scope in the [first-production contract](docs/briefs/production-contract.md). Experimental comparison paths have no production compatibility requirement. A command interceptor validates execution preconditions and fulfillment without rewriting SQL text. Preserve the single runtime assembly and native transaction ownership; additional query shapes require a contract amendment and provider proof.

Record actual versions and results with the change evidence. Distinguish compilation, unit tests, real PostgreSQL execution, package construction, isolated packed-consumer execution, and publication. None implies another. Do not claim a compatibility range or released package from local compilation or `dotnet pack` alone.
