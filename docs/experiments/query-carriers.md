# Query carriers: experimental observations

This is a research/evidence record, not a production admission contract. The local source commit is `7e79908ad51e2750814f44ba86b1744fa22cb613`. It has not been pushed. The [machine-readable observations](query-carrier-observations.json) retain every test name, Q01–Q18 disposition and recorded SQL/session observation. They contain 54 query/carrier cells and 166 observation lines from one current run: 130 passed, zero failed or skipped. The separate unit-only command passes 9 tests; those overlap the full run and are not added to its total.

## Reproduce and interpret

Use that source commit and the commands in [CONTRIBUTING](../../CONTRIBUTING.md). Inputs: SDK 10.0.401, EF Core/Relational 10.0.12, Npgsql EF provider 10.0.3, Npgsql 10.0.3; Windows .NET host and Linux amd64 PostgreSQL 16.14 container, pinned in the fixture by digest. Default Npgsql SQL target is 14.0; a separate two-context 14/15 target smoke is included. One exact package set is characterized. No other patch, server, platform or support range is certified. Remote Linux/Windows workflows are configured and have not been executed.

The full command uses real PostgreSQL, distinct connections and explicit transactions. Shape-rejection controls run inside a valid transaction, assert the intended reason and zero issued commands. Missing-transaction controls are separate. Native SQL controls and observable PostgreSQL wait/error results supply independent oracles; generated SQL text alone is insufficient.

## Comparison

| Carrier | Intent transport and replaced factories | Observed scope | Maintenance boundary |
|---|---|---|---|
| A — TableAnnotations | Typed marker, Npgsql-derived QMT, immutable replacement table annotation, derived generator | Selected root forms pass; listed negative forms reject | Depends on mutable `SelectExpression.SetTables`, annotation copying and both provider factory seams |
| B — TranslatorTag | Typed marker, Npgsql-derived QMT, validated reserved tag, derived generator | Same characterized forms/dispositions | Avoids table rewriting; retains two provider factory seams and strict tag/scope validation |
| C — ExpressionTag | Typed marker, compilation interceptor, validated reserved tag, derived generator; native QMT retained | Same characterized forms/dispositions | Replaces only the SQL generator factory; still depends on pinned provider internals, extraction timing, cache identity and command-time validation |

Recommendation: **NARROW, with C as the proposed production candidate** because it preserves the native query translator while satisfying this bounded corpus. This recommendation is not a carrier selection or stable API approval. All three remain comparison probes in this experimental assembly. Production admission must remove losing active paths and establish an exact supported contract, guards and package consumer proof.

| Case | A / B / C actual disposition |
|---|---|
| Q01 | Supported: id-equality root UPDATE lock, exact selected/non-target rows |
| Q02 | Supported: marker before predicate, same root lock scope |
| Q03 | Supported: no tracking and SHARE compatibility/conflict |
| Q04 | Supported: OrderBy/Take(1), actual bound limit and row locks; OFFSET case explicitly rejected |
| Q05 | Tested inner JOIN shape rejected before SELECT |
| Q06 | Tested nullable-side LEFT JOIN shape rejected before SELECT |
| Q07 | Tested self-JOIN shape rejected before SELECT |
| Q08 | Tested DTO projection rejected before SELECT |
| Q09 | Tested split-only and real collection Include/split rejected before SELECT; invalid-navigation control retained separately |
| Q10 | Marked nested subquery and native Take/filter pushdown rejected before SELECT |
| Q11 | Tested Distinct, GroupBy, Count and Sum rejected before SELECT |
| Q12 | Tested Union, Except and Intersect rejected before SELECT |
| Q13 | Ordinary user tag preserved with real lock; reserved/multiline spoofs rejected with and without a marker |
| Q14 | Identical and conflicting repeated requests explicitly rejected |
| Q15 | Sync/async compiled forms pass with real second-session lock checks; native/unmarked and factory/pool reuse controls pass |
| Q16 | Dynamic compiled options rejected for nonconstant structural arguments inside a valid transaction |
| Q17 | Repeated registration passes; prior and late query-service replacement, missing configuration, wrong provider and external internal-provider limitations fail explicitly; NodaTime native translation and independent context/target smoke pass |
| Q18 | Unmarked native/enabled SQL and results match in tested forms |

These are specific observations, not a proof about every LINQ expression in a case family. Arbitrary unmarked predicate subqueries, global query filters and untested mappings remain uncharacterized and are not admitted for production. Unsupported cases do not authorize an unlocked fallback.

## Runtime and independent mechanisms

All carriers show real NOWAIT `55P03`, wait cancellation, borrowed transaction and commit/rollback release, additional strengths and SKIP LOCKED. Native EF transient wrappers retain inner SQLSTATE; the extension does not unwrap or retry. Independent native controls characterize generated-query serialization `40001`, deadlock `40P01` and terminated backend `57P01`.

The READ COMMITTED sort-key scenario observes a lock wait, then changes the holder's first row Rank from 10 to 30 and commits. Native and generated queries both return that originally selected row with Rank 30, while the other row is lockable and the returned row remains locked. ORDER BY does not provide a general post-wait sorting or acquisition-order guarantee.

Advisory commands execute parameterized native `SELECT` of the transaction-scoped function through `DatabaseFacade`. Independent `pg_try_advisory_xact_lock(hashtextextended(text,0))` verifies the exact server hash/seed; key spaces, borrowing, release and cancellation are exercised. No row-count business meaning is assigned to this SELECT.

The model-mapped scalar clock differentiates volatile `clock_timestamp()` from transaction `now()`, preserves UTC/precision and demonstrates sampling/reusing one server value. It needs no query-factory replacement. Atomic insert-ignore exercises generated/default values, a composite target, a converter, nullable uniqueness, secondary-constraint `23505`, a competing insert race and caller-transaction visibility/rollback without tracking changes. Unsupported partial/shadow/split mappings fail before opening a connection.

Native transaction read-only and write failure/rollback behavior are observed. Read-only capability checks separate configured target from actual server and required extensions from installed inventory; incompatible requirements/target, unreadable facts and cancellation remain distinct. No extension installation, per-query discovery or host lifecycle is supplied.

## Local package and unresolved gates

`dotnet pack -c Release --no-build -o artifacts/packages` produces local preview artifacts from the source commit above. Package metadata and Portable PDB SourceLink refer to that commit; document paths are normalized to `/_/`. The source commit is local, so remote SourceLink availability is not claimed.

| Artifact | SHA-256 |
|---|---|
| `EntityFrameworkCore.PostgreSQL.Extensions.0.1.0-preview.1.nupkg` | `736e3464c7fa904da0bed81145196a5ed2f624a9e6c9fd1222c22ba4ed1da0d0` |
| `EntityFrameworkCore.PostgreSQL.Extensions.0.1.0-preview.1.snupkg` | `a335c5859a2c2611a817604e97c7da779f14b15b39de19a0f7a95f6a9ac84236` |

File/history/metadata/package text scans and source review preserve Apache-2.0 and find no private consumer context or credentials in the review candidate. These bounded checks are not a universal secret-detection guarantee. No package has been published. An isolated packed consumer, production support policy, stable API and maintainer acceptance of the recommendation remain separate gates.

## Failed attempts and repairs

Exploration exposed actual provider API/visibility mismatches, discarded replacement-table annotation and enum parameter extraction. Corrections use the pinned public/provider seams, retain the returned annotated expression, and mark structural options with EF's `NotParameterized` attribute. No tag fallback or SQL text rewrite was introduced.

Test repairs preserve native microsecond precision, parameterized/composed LIMIT and native EF exception wrappers. The intentional configuration matrix logs only EF's provider-count warning before options freeze; caching and other warnings remain enabled. Independent review required valid-transaction negative oracles, consistent compatibility-gate wording and the concurrent sort-key experiment. Those cases are included in the current run. One accidentally dispatched stale-binary attempt after a failed build is excluded entirely from positive evidence. Failed attempts remain failed; they are not reclassified as successful runs.
