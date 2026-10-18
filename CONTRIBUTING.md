# Contributing

Read [AGENTS.md](AGENTS.md), the [code instructions](.agents/code.md), the [documentation instructions](.agents/documentation.md) and the guide for the mechanism you change. Public contracts live in the [documentation](docs/index.md); [architecture](docs/architecture.md) explains the implementation.

## Build and test

The runtime is a standalone .NET 10 library. `global.json` requests SDK `10.0.100`, allows `latestFeature` roll-forward and disables prereleases; the reference resolution is `10.0.401`. Restore needs NuGet access. Full PostgreSQL tests and the isolated package consumer need Docker or a compatible container runtime.

From the repository root:

```powershell
dotnet restore
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release --logger trx
```

For unit checks without PostgreSQL, after building:

```powershell
dotnet test --no-build --configuration Release --filter Category=Unit --logger trx
```

The test fixture pins PostgreSQL `16.14`. Linux CI runs the full suite, packs the library and invokes the isolated package consumer. Windows CI builds and runs unit tests. Tests involving concurrency must observe actual overlapping PostgreSQL operations, use independent sessions, and bound cancellation/cleanup. A generated SQL string alone does not prove locking or atomicity. Negative shape tests run with a valid transaction, assert the intended rejection and observe zero issued commands.

Query changes require the relevant original-expression/model, final SQL, service-composition, compiled/cache/factory/pool and real two-session controls. Cover target/non-target locks, modes, release, cancellation and native errors as appropriate. Other mechanisms require their own positive/negative checks: selected/unrelated insert conflicts, defaults/NULL/converters/tracking, server clock semantics, read-only enforcement, probe outcomes and connection ownership. Keep native SQL and unmarked-query controls. Dependency support claims require checks for the newly claimed combinations.

## Check the package

```powershell
dotnet pack src/EntityFrameworkCore.PostgreSQL.Extensions --no-build --configuration Release --output artifacts/packages
pwsh ./scripts/Test-Package.ps1 -PackagePath ./artifacts/packages/EntityFrameworkCore.PostgreSQL.Extensions.0.1.0-preview.6.nupkg
```

The runner copies the supplied archive into a fresh local feed/cache, restores and builds the tracked consumer template outside the checkout, verifies the package/loaded assembly bytes, and executes against PostgreSQL. It uses a `PackageReference` with no source/project reference. `-EvidenceDirectory` may point to ignored `artifacts/` or an external directory; `-KeepWorkspace` retains the temporary workspace for diagnosis. Keep results in the response/PR and temporary artifacts outside version control.

Inspect package identity, dependency intervals, license, embedded README, XML documentation and source metadata when they change. Package construction, provider execution, isolated consumption and publication are separate checks. Versioned artifacts already selected by consumers remain immutable; a README-only repack is still a different archive. Local documentation development does not authorize replacing an adopted package under the same version or publishing a new release.

New public capabilities and expanded support claims require independent substantive review and successful relevant CI/provider/package-consumer checks on the same candidate. A scope reduction is agreed and documented before relying consumer changes. Preserve attribution and check file/history/package metadata before public exposure of newly imported material.

## Work on the documentation

Use Node.js 24 and npm. From the repository root:

```powershell
npm ci
npm run docs:dev
```

The VitePress website reads `docs/`. English pages are at its root, Russian counterparts under `docs/ru/`. Three guides group capabilities: locking, data/transactions and configuration/diagnostics. Getting started, compatibility and architecture provide shared context. Keep paths, explicit anchor IDs, examples and technical conditions aligned in both languages. Code changes affecting usage, behavior, configuration or support must include their documentation updates. Keep current contracts in one owning section and link to it.

Before delivering:

```powershell
npm run docs:build
npm run docs:preview
git diff --check
```

The build checks both languages and internal page links. Preview uses the project base path `/EntityFrameworkCore.PostgreSQL.Extensions/`; open that path on the reported local server. Check the locale switch, translated navigation/search, section anchors, code blocks and narrow layouts. Compile changed C# examples where applicable. Build success does not establish prose quality or translation equivalence.

Markdown remains readable on GitHub. The README uses absolute repository URLs so its links also work when embedded in a NuGet package. Keep instructions readable without a deployed site. Node tooling is confined to documentation; .NET builds do not require it.

## GitHub Pages

The `Documentation` workflow builds both locales on pull requests and pushes to `develop`. It also supports a manual run with `publish` disabled by default. It does not deploy from pull requests or ordinary pushes.

To set up the project site:

1. Merge the documentation change into the default branch, `develop`. GitHub exposes the manual workflow trigger after the workflow file exists on the default branch.
2. Open the repository's **Settings → Pages**. Under **Build and deployment → Source**, select **GitHub Actions**. This initial setup requires repository administration access.
3. Open **Actions → Documentation → Run workflow**. Select `develop`, enable `publish`, and run the workflow. The caller needs write access. Publication is accepted only from `develop`.
4. Wait for both `build` and `deploy` to succeed. Use the URL shown by the `github-pages` deployment. With the repository's current name and the standard GitHub domain, English is served at `https://keeyahto.github.io/EntityFrameworkCore.PostgreSQL.Extensions/` and Russian at `https://keeyahto.github.io/EntityFrameworkCore.PostgreSQL.Extensions/ru/`.

The workflow uploads `docs/.vitepress/dist`, checks the enabled Pages configuration and deploys through the `github-pages` environment. The supplied `GITHUB_TOKEN` and job permissions handle deployment; no personal access token, custom secret or paid hosting is needed for this public repository. Initial Pages enablement is done in repository settings. Custom domains are optional; update the VitePress base deliberately if adopting one or renaming the repository. Subsequent updates are published by repeating the manual run on `develop`. Site publication and NuGet publication are independent actions.

## Changes and repository hygiene

Keep changes scoped, explain the public effect, and report actual validation plus anything that could not run. Update both user-guide languages, relevant XML comments/examples and the changelog when behavior changes. Preserve the documented grammar, caller-owned transactions, native errors and independent mechanisms. Private consumer requirements, governance and data stay outside this repository.

Add only requested deliverables to Git. Do not add unsolicited evidence bundles, test logs, acceptance/review reports, handoff documents, new plans or agent journals. Temporary diagnostics belong in ignored `artifacts/` or outside the checkout. Superseded documentation is removed after preserving useful current contracts; its history remains in Git. Do not create an archive merely to retain obsolete material. Check the actual file list before staging and preserve unrelated changes. Package publishing and site deployment require an applicable user instruction and account permissions.
