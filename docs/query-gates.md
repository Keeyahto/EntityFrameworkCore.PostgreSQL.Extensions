# PostgreSQL query integration gates

These gates define evidence needed for the selected production row-lock implementation. The implementation is one expression-interceptor path: original-expression and model validation, immutable intent in a reserved internal tag, native Npgsql translation, structural lock generation for the exact root alias, and command-time fulfillment/transaction/bound validation. The [first-production contract](briefs/production-contract.md) owns the finite intended scope and compatibility target; [query integration](query-integration.md) owns the semantic contract. Gate definitions do not record that a run passed or that maintainers admitted a release.

An intended admitted form is a positive oracle: the provider execution must succeed and prove its promised behavior. Do not count rejection of an admitted form as an acceptable alternate result. Unsupported forms are negative oracles with their own expected cause, a valid caller transaction, and zero issued commands.

## Gate 1 — Original expression and model

For every admitted expression, verify the exact EF operator/method identity and overload, direct entity root, marker count and placement, direct mapped members, and predicate/order grammar before translating it. Equality and conjunction cases must use the root scalar property and a constant or EF-extracted parameter; terminal predicate overloads follow the same grammar. Validate finalized model metadata as well: exactly one directly mapped table occurrence, no declared named or anonymous filters, and no inheritance, fragments, views, raw/function mappings, shared-table entities, complex properties, or eager navigations.

Exercise scalar primary/natural/composite/text/nullable/converted-key predicates, tracking modes, the admitted marker placements, entity enumeration, single-entity terminal forms, and the bounded ordering form against real PostgreSQL. Assert the terminal's empty/multiple-result behavior. Every admitted form must return the expected entity result rather than merely compile or produce a plausible SQL string.

Run negative cases for disjunction, inequality, method/navigation predicates, predicate subqueries, unsupported terminal operators, named and anonymous filters, raw roots, unsupported mappings, identity projection, invalid order keys, repeated markers, dynamic lock options and unknown operators. These cases must fail for the intended validation reason before any SELECT is issued.

## Gate 2 — Final relational scope and selection bounds

Inspect the final relational tree and generated SQL to establish that exactly one mapped root occurrence remains and that the structural `FOR` clause names its generated, quoted alias. Check that projections, predicate columns, ordering columns and any limit refer to that same alias. A root-looking final SELECT cannot override an invalid original expression or model.

Prove the command behavior on PostgreSQL using two independent connections and explicit transactions. A conflicting operation on the selected row must wait or fail according to the selected mode; an operation against a non-target row must remain independent. Exercise the claimed lock strengths, wait/no-wait/skip-locked behavior and release on both commit and rollback. Preserve the native SQL and lock behavior as controls. SQL snapshots or `pg_locks` observations alone do not establish the required runtime effect.

For `OrderBy`/`ThenBy` followed by `Take(1)`, observe both selected result and target lock. `Take(1)` may be represented by an EF-generated terminal limit or an extracted private parameter. Confirm the exact bound equals one at command execution, including native `LEAST` composition with First/Single limits. Warm a cached parameterized `Take(1)` query, then execute the same compiled form with `Take(2)`; it must reject before issuing the second SELECT. Do not infer a global lock-acquisition order or post-wait row ranking from `ORDER BY`.

Reject joins, multiple targets, projections, Include/split query, nested relational SELECTs, offset, arbitrary/repeated `Take`, distinct/grouping/aggregates/set operations, and other unsupported final structures before the marked SELECT. Keep the expected reason specific enough to distinguish scope validation from missing registration, service replacement, or transaction failure.

## Gate 3 — Command, cache, transaction and service composition

At command execution, require exactly one matching internal request and fulfillment receipt; the exact selected generator and native Npgsql translator; the current explicit EF transaction object on the same command connection; and any exact `Take(1)` execution parameter. Query construction or SQL inspection may occur before a transaction exists, but execution may not. A correctly attached borrowed EF transaction is eligible. Ambient `TransactionScope` alone is outside the contract.

Verify missing registration, wrong provider, generator/translator replacement before and after cache population, and incompatible manual internal service-provider composition all fail without a marked SELECT. Repeated registration is idempotent. Verify `AddDbContext`, context factory, pooled contexts including size-one reuse, separate context/provider-target isolation, and NodaTime in both registration orders. Compiled synchronous/asynchronous queries and reuse across contexts must preserve intent without leaking it to ordinary queries. An unmarked query must preserve native Npgsql SQL and results.

## Gate 4 — Native PostgreSQL outcomes

Use real PostgreSQL transactions and two independent connections to prove target/non-target lock scope, strength and wait compatibility, release on commit/rollback, borrowed-transaction ownership, and cancellation of a blocked lock. Retain controls for no registration, a native unmarked query and feature-off options. Verify native lock-unavailable, serialization, deadlock, cancellation and backend-termination outcomes through EF/Npgsql wrappers and SQLSTATEs. The library must not rewrite command text, create/complete a caller transaction, retry, or translate provider failures into domain outcomes.

## Gate 5 — Package consumer and CI

Run the applicable repository CI checks: the Linux workflow covers restore, Release build, full real-PostgreSQL tests, pack and isolated package consumption; the Windows workflow covers restore, Release build and unit tests. Workflow configuration is not evidence of a successful run. Inspect the packed artifact, then install that exact local package into a fresh isolated neutral application with no project/source reference. The consumer smoke must exercise supported registration forms and positive plus fail-closed scenarios for the included features against real PostgreSQL. Build, test, pack, isolated consumption, publication and supported-version claims remain separate results.

The provider SQL-generator seam uses Npgsql internals and is version-sensitive. Compatibility claims must be backed by evidence for the exact tested dependency/server combinations; maintained documentation states only adopted claims. The first-production contract's initial pins do not imply a wider patch or server range, and package identity or publication remains a maintainer decision.

## Independent mechanisms

The query plugin is not required by the independent clock mapping/read, advisory-lock commands, insert-ignore DML, capability probe or read-only transaction command. Apply their own real-provider gates from the [first-production contract](briefs/production-contract.md): preserve volatile `clock_timestamp()` versus transaction time and connection/transaction ownership; prove advisory-lock contention and release; exercise atomic insert conflict selection, converters, sentinels/defaults, explicit NULL and tracking boundaries; and validate capability-probe/provider-failure behavior. Do not use query-plugin tests as substitutes for those mechanism oracles.

## Exclusions and admission

The supported query set remains the closed set in the first-production contract. General JOIN/multi-target locking, projections, ordered multirow/key-set protocols, raw user target aliases, arbitrary SQL AST rewriting, command-text patching, hidden transactions/retries, automatic SQL-target selection and untested mappings remain outside the initial contract. `ORDER BY` does not promise a global acquisition sequence; `SKIP LOCKED` provides no completeness or fairness guarantee; `Single` may lock inspected rows before reporting multiplicity. Additional shapes require contract amendment, focused positive and negative tests, and real-provider evidence before admission.

Compilation, a generated SQL assertion, unit tests, a local provider run, a package build, configured workflows, or this gate list alone do not establish production admission. Required mechanism-specific provider evidence, isolated-consumer execution, current CI results and independent maintainer review must align with the same candidate before making an admission or compatibility claim.
