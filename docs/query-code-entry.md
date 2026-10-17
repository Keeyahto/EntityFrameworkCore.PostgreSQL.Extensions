# Query integration implementation and proof boundary

The production implementation uses one expression-interceptor path for typed row-lock requests. The interceptor validates the original query and finalized model, carries immutable intent in a reserved internal tag, and retains Npgsql's native query translator. A structural Npgsql SQL generator emits the lock for the validated root alias. A command interceptor checks the generated fulfillment, active EF transaction, service composition, and execution-bound selection parameter; it does not change SQL text. The selected design and exact finite target are defined in the [first-production contract](briefs/production-contract.md), and query semantics are specified in [query integration](query-integration.md).

The public registration is parameterless:

```csharp
options.UseNpgsql(connectionString,
    npgsql => npgsql.UsePostgreSqlExtensions());
```

The former annotation and translator comparison carriers and the `PocCarrier` switch have no production path. They remain historical research only. Keep the runtime topology to one library project and one test project; provider-internal integration remains version-sensitive.

## Implementation and proof workflow

For each change to row-lock behavior, keep evidence at every point where intent can be lost or widened:

1. **Original expression and model.** Check exact EF method identities and overloads, the direct root entity, admitted predicate/order grammar, finalized mapping, declared query filters, and marker multiplicity. Operator names alone do not admit an expression.
2. **Final relational tree and SQL.** Check that one mapped table occurrence, its generated alias, predicate/order columns, and any selection limit survive translation. Inspect generated SQL structurally and execute the query against PostgreSQL. A matching `FOR ... OF` string alone does not prove target scope.
3. **Command and cache.** Confirm the request and generator receipt match, the native Npgsql translator and selected generator are still installed, and an extracted `Take(1)` parameter equals one for this execution. Warm-cache and compiled-query reuse must not make a `Take(2)` request execute.
4. **Transaction and server behavior.** Execute only in the current explicit EF transaction on the same connection. Use two independent PostgreSQL connections and transactions to observe target-row blocking, non-target independence, wait modes, and release on commit or rollback. Preserve native lock errors and cancellation.
5. **Composition and consumer.** Exercise native unmarked queries, additive NodaTime registration, factories, contexts and pools, then build and inspect the package and run it from an isolated consumer without a project or source reference. Required CI and the consumer check are separate evidence.

An admitted form is a positive contract and its test must succeed. It must not be treated as a candidate that can pass by either succeeding or rejecting. Unsupported forms are separate negative cases: execute their assertions inside a valid transaction, verify the intended rejection, and observe zero issued commands. Preserve native controls so the test distinguishes extension behavior from ordinary EF/Npgsql behavior.

## Current query target

The target is one directly mapped, unfiltered entity root with one unambiguous table occurrence. Tracking and no-tracking entity materialization are eligible. Predicates are direct scalar equality against a constant or extracted query parameter, joined only with `AND`; primary, natural, composite, nullable, text, and explicitly verified converted-key fixtures have corresponding provider execution coverage. Marker placement before or after an admitted `Where` is covered. Supported terminal forms preserve native empty/multiple-result behavior. Ordered selection is limited to direct mapped scalar `OrderBy`/descending and optional `ThenBy`/descending followed by `Take(1)`. Synchronous/asynchronous entity enumeration and compiled-query forms have their own execution coverage. Lock mode and wait mode are structural options, not runtime SQL-keyword parameters.

This tested surface is distinct from a broad claim that every EF expression or mapping is supported. The complete intended finite scope, compatibility target and limitations live in the first-production contract. In particular, the following remain rejected unless a separately reviewed contract amendment and provider proof admit them: joins and multiple targets; projections including identity `Select`; eager Include and split query; global filters; navigations, methods, `OR`, inequalities, predicate subqueries and `Any`/`Count`; raw SQL roots; inheritance, views, shared or split table mappings, complex properties and eager navigations; nested relational selects; offsets; arbitrary or repeated `Take`; distinct/grouping/aggregates/set operations; repeated lock markers; dynamic lock options; and unknown shapes. No rejected marked query may return data as if it had been locked.

PostgreSQL locks the exact generated root alias. `ORDER BY` does not establish a general lock-acquisition order or post-wait ranking guarantee at READ COMMITTED. `SKIP LOCKED` may omit occupied rows. `Single` can lock inspected rows before reporting multiplicity. The caller owns the transaction and any business policy; the library creates no transaction, retry, commit, release policy, or domain outcome.

## Registration and native behavior

Repeated registration is idempotent. A competing query-generator or translator replacement before, during, or after registration must fail explicitly; last-registration-wins behavior is not supported. NodaTime is additive and must continue to translate in either registration order. `AddDbContext`, `AddDbContextFactory`, pooled contexts and size-one pool reuse are distinct composition cases. Manually assembled `UseInternalServiceProvider` configurations are unsupported and must fail clearly. Query construction and SQL inspection may precede a transaction; marked command execution requires the active explicit EF transaction on its command connection. Ambient `TransactionScope` alone does not satisfy that precondition.

Ordinary unmarked queries retain native Npgsql SQL and results. Native provider exceptions remain recognizable through EF/Npgsql wrappers, including lock conflict, serialization failure, deadlock, cancellation and backend termination. The extension does not retry or classify a domain result.

## Scope boundary

Use [architecture](architecture.md), [compatibility](compatibility.md), and the [first-production contract](briefs/production-contract.md) as current owners of architecture, compatibility and supported behavior. This entry describes implementation and evidence practice; it does not record acceptance results or create additional supported shapes. Exact package/server combinations and candidate evidence remain with change/evidence owners. A package build is not isolated-consumer evidence, and configured CI does not establish a successful run. Publication remains a separate maintainer-authorized action.

Do not add a universal SQL operation base, another DI framework, a capability registry, automatic SQL-target switching, hidden transactions/retries, or command-text lock rewriting. Keep the four mechanisms independent and use native EF/Npgsql behavior where it supplies the required semantics.
