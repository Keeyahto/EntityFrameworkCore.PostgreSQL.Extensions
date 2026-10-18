# Compatibility

These guides describe package **0.1.0-preview.6**, one `net10.0` runtime assembly. The API is a preview and may change. Public NuGet package availability and a stable API commitment have not been established. [Getting started](getting-started.md) uses a locally built package.

<a id="reference-api"></a>
## Reference combination

| Component | Reference value |
|---|---|
| Target framework | `net10.0` |
| Repository SDK request | `10.0.100`, `latestFeature`, prereleases disabled |
| Reference resolved SDK | `10.0.401` |
| EF Core Relational | `10.0.12` |
| Npgsql EF Core provider | `10.0.3` |
| Npgsql | `10.0.3` |
| Npgsql NodaTime plugin for coexistence checks | `10.0.3` |
| PostgreSQL | `16.14` |
| Npgsql SQL-generation target | `14.0` |

The PostgreSQL fixture uses `postgres:16-alpine@sha256:33f923b05f64ca54ac4401c01126a6b92afe839a0aa0a52bc5aeb5cc958e5f20`. This digest is a reproducible test input. The separate SQL-target `15.0` context-isolation check does not establish server-14/15 support.

The SDK request permits feature-band roll-forward; consumers are not required to pin SDK `10.0.401`. The library does not change a consuming application's SDK policy.

<a id="dependency-api"></a>
## Dependency ranges and support

Package metadata declares EF Core Relational `[10.0.12,11.0.0)` and Npgsql/provider `[10.0.3,11.0.0)`. Those intervals allow NuGet resolution within major 10. They do not certify every admitted patch. The table above is the reference combination for provider and package checks; assess a different combination against its own verification.

The row-lock SQL generator uses version-sensitive provider internals. Updating EF/Npgsql requires relevant compilation, real-provider and isolated package-consumer checks. Extra TFMs, EF Core 8/9/11, other PostgreSQL patches/majors, NativeAOT and trimming are outside the verified support described here. Broader support needs an explicit decision and checks for the additional boundaries.

<a id="feature-api"></a>
## Capability boundaries

| Family | Current scope |
|---|---|
| [Locking](query-integration.md) | Closed root-entity grammar, complete ordered UUID sets and transaction-scoped advisory keys; general JOIN/projection remains outside the lock grammar |
| [Data and transactions](internal-mechanisms.md) | One-row selected-key insert-ignore, clock mapping/typed read and read-only command with documented mapping and transaction limits |
| [Configuration and diagnostics](capabilities.md) | Explicit read-only database requirement probe; application owns readiness and target selection |

Supported registration includes normal contexts, factories and pools with the Npgsql provider; manual internal service-provider composition and competing query-generator/translator replacements are excluded. Additive NodaTime behavior is checked separately. No automatic transaction, retry or application recovery policy is supplied.

<a id="release-api"></a>
## Verification and releases

The repository's Linux workflow builds, runs the full provider suite, packs and executes an isolated package consumer against PostgreSQL. The Windows workflow builds and runs unit tests. Full provider and packed-consumer execution require Docker or a compatible container runtime. Commands are in [Contributing](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/CONTRIBUTING.md).

A support claim needs the corresponding actual results. Build, provider execution, local package construction, isolated consumption and publication each establish different facts. Old run reports and test counts are retained through Git history; current guides state contracts and instructions. Neither a configured workflow nor this page establishes a successful current run or a published release.

Once an artifact is released or selected by a consumer using an exact version/hash, keep it immutable. Publish a changed artifact under a new version. Documentation-only edits can be committed independently; if an embedded README is repackaged for distribution, it is a different package artifact. Release notes belong in the changelog, and package publication requires maintainer authorization and actual account rights.
