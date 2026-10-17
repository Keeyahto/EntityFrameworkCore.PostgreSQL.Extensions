# EntityFrameworkCore.PostgreSQL.Extensions — экспериментальный план перед freeze API

The repository contains experimental implementations of the mechanisms and one test project. Bounded local PostgreSQL checks exercise selected row-lock, advisory-lock, DML, clock, and capability-probe behavior. The query carrier and public API scope remain experimental pending maintainer review; arbitrary unmarked-predicate subqueries and global query filters are uncharacterized. These pilots define proof and admission criteria; detailed run results belong with change evidence.

## PILOT-00 — Repo bootstrap, package smoke

Готовность: чистый clone → `dotnet build` → `dotnet test` → `dotnet pack` на .NET 10 SDK из `global.json` с flexible feature roll-forward. Independent package metadata, GitHub commit SourceLink, `EnablePackageValidation`, NuGet smoke sample using packed nupkg. Real Docker/PG availability documented. No downstream application project reference. Exit: package can be imported in a separate sample using this repository alone.

## PILOT-01 — Lock integration (hardest provider seam)

Compare the three implemented structural carriers:
- A: typed `TableExpression` annotation, Npgsql query-method-translator visitor, and SQL generator.
- B: Npgsql query-method-translator visitor, reserved query tag, and SQL generator.
- C: query-expression interceptor, reserved query tag, and SQL generator.

For each, inspect SQL scope/aliases, query cache and compiled expressions, nested queries, `ORDER/LIMIT`, plugin composition, missing registration and fail-closed behavior. A command interceptor may validate execution preconditions and carrier fulfillment; it must not alter SQL text.

Positive (minimum): `.Where().ForUpdate().SingleAsync`, `FOR SHARE`, tracked/untracked, 2 sessions blocking, tx commit/rollback release, no transaction failure, `SELECT` only.

Negative: `Distinct`, `GroupBy`, `Union`, projection omitting locked entity, nullable side of LEFT JOIN, split query, provider mismatch, pooled context, no transaction, duplicate plugin install, another interceptor. Support each only with explicit proven translation; otherwise fail-loud.

Gate: choose implementation by correctness/composability, stable support burden, diagnostics, version compatibility, third-party plugin interaction, public simplicity. No blind SQL rewriting or pseudo-LINQ ignoring lock request. Library may start without this capability if it fails high-assurance tests.

## PILOT-02 — Transaction-scoped advisory locks

Implement typed key variant based on `DatabaseFacade`, under current `Database.CurrentTransaction`. Verify `pg_advisory_xact_lock(hashtextextended(@text,0))` parity to application legacy; two sessions same key serialize; independent keys do not; lock auto release on tx commit and rollback; no session lock leak; command with borrowed transaction; cancellation. Validate keyspace & collision documentation. No .NET hash.

## PILOT-03 — Atomic insert-ignore

Start with one-row `INSERT ... ON CONFLICT (<mapped unique columns>) DO NOTHING`, return affected rows/bool, EF metadata correct quoted SQL and values; same transaction; no unexpected tracking state changes. Test 5 **generic** sample shapes (identity/default, composite unique, converted column, nullable key, collision with secondary unique index). Prove no check-then-insert. Explicitly reject unsupported computed/filtered-key semantics or implement fully if necessary. `DO UPDATE` is out of MVP.

## PILOT-04 — PostgreSQL database clock

`clock_timestamp()` via EF SQL function translator, first-class SQL parameterization, no client fallback; compare `now()` under long tx, UTC mapping, DateTimeOffset vs DateTime, TTL correct, independent exactly-once sampled timestamp. Prove whether registered DbFunction is sufficient without query-gen service overrides.

## PILOT-05 — Independent consumer + release-readiness proof

Small sample app in second solution consumes local `.nupkg`, builds, uses all enabled capabilities, no downstream application symbols or migrations, with `AddDbContextFactory` and `AddDbContextPool` variants, readme code compiles. Simulate dependency version mismatch and missing registration. Security docs, license, package metadata, source link, stable API snapshot and initial SemVer preview version. Review false claims (AOT, multi-TFM, PG versions, unsupported shapes).

**Success:** each included public primitive behaves correctly on real PostgreSQL with negative/transaction tests; incompatible query shape is rejected *before unlocked behavior*, API usable from native EF context; exact one-line registration when needed; no unrelated EF capability regression. Failed spike must result in narrowed public contract, not undocumented fallback.


## Package compatibility evidence

`PILOT-05` is the evidence path for an isolated consumer using the packed `.nupkg`. A future release smoke may cover a selected minimum and latest verified `EF Core 10.x + Npgsql EF provider 10.x` pair. No minimum supported revision is declared yet. The configured Linux workflow runs the current pinned full integration suite; it does not run a packed consumer. Do not add an untested compatibility promise, custom matrix framework, or claim consumer evidence until that smoke runs.
