# Local preview package verification

This change record concerns source `e8ec7b8259a7056f490ae4471cccef86d5844c78` and local package `0.1.0-preview.2`. The [first-production contract](../briefs/production-contract.md) defines the finite admitted scope. The machine-readable [artifact record](package-verification.json) contains exact hashes and observations. This record establishes no NuGet publication, stable API or compatibility with untested dependency/server revisions.

## Reference execution

The reference set is .NET SDK 10.0.401, `net10.0`, EF Core Relational 10.0.12, Npgsql EF Core/Npgsql/NodaTime 10.0.3, and the pinned PostgreSQL 16.14 fixture. SQL target 14 and a separate target-15 context-isolation check are distinct from actual server versions.

Release build succeeds with warnings treated as errors. The full suite passes 85 tests with no skips, including 81 PostgreSQL cases and 4 unit cases. A separate unit selection passes the same 4 unit cases; these totals overlap. Required proof includes native two-session locks and release/errors/cancellation, finite query/model rejection, compiled/cache bounds and service composition, advisory key/hash semantics, atomic insert races/mappings/tracking, clock volatility and typed reads with NodaTime, read-only enforcement and capability outcomes.

The exact local package is installed in an OS-temp consumer outside the checkout using a new local feed and package cache. Its project has `PackageReference`, no source/project reference and no repository build-property import. Restore, build and real PostgreSQL execution pass. The cached `.nupkg` hash equals the supplied artifact; the runtime-loaded DLL hash equals the packaged DLL. The consumer exercises registration/factories/size-one pools, NodaTime in both orders, row-lock target/non-target behavior and negative controls, advisory hashing, selected/unrelated insert conflicts and rollback, both clock APIs, read-only writes and capability results/cancellation. The owned workspace is removed after execution.

Pack inspection checks the exact source repository commit, license expression, README, public XML documentation, net10.0 assembly and symbols, dependency intervals and absence of private text/local source paths. Portable PDB documents use `/_/` paths; SourceLink resolves the source commit above. Only the exact reference dependency set is exercised; the major-10 dependency intervals do not certify every patch within them.

## Corrections retained with execution evidence

Initial runs exposed exact-overload selection for `Take`, own-service idempotence, null-schema intent encoding, invalid negative-fixture/oracle setup and conflicting transactions inside a plugin test. They were corrected before the reference run. The isolated consumer exposed a typed clock issue with NodaTime's preferred object-return type; explicit `GetFieldValue<DateTime>` preserves the API's UTC DateTime contract. Original user tags now reject reserved protocol text before the unmarked-query fast path; a valid forged request/receipt pair is a zero-command negative control.

Earlier failed executions and diagnostic artifacts remain failed research attempts. They are separate from the exact source/artifact observations above. A package-inspection helper initially expected license metadata in a symbols-package nuspec; inspection was corrected to validate the runtime nuspec's Apache-2.0 expression and the symbols package's own repository/dependency/PDB metadata.

## Controlled insert contention and review disposition

Independent review of provisional source `91df7054a936f927e897d87492d00d35250104d9` identified F01: launching two insert tasks together could pass sequentially and did not establish an actual collision. That candidate remains recorded as FIX_REQUIRED with its original artifact hashes and successful, insufficient test/CI observations in `previousCandidate` of the artifact record.

The current source replaces that oracle with two attached explicit transactions. The first insert remains uncommitted; an independent observer sees the second backend active on the composite-key INSERT, waiting on `Lock`, with the first backend in `pg_blocking_pids`. Only after observing this conflict does the test assert the second call remains pending. First commit yields second=false and one row carrying the first payload; first rollback yields second=true and one row carrying the second payload. Both cases log the observed blocker/query and outcomes. Failure cleanup cancels and awaits a pending insert before transaction cleanup. Both cases pass in the exact current full provider run; fresh package inspection and isolated consumption also pass.

GitHub Actions [run 39061281358](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/actions/runs/39061281358) succeeds for the exact current source: Linux completes full provider tests, pack and isolated consumption; Windows completes build and the unit selection. Independent substantive final review passes for the current source/artifact and evidence, including the controlled-overlap repair and cleanup. No required admission obligation remains within this finite library scope. Publication, NuGet account rights and downstream transaction/permission equivalence remain separate gates.
