# Query integration implementation and proof boundary

The repository contains A/B/C row-lock carrier implementations. Bounded local PostgreSQL checks cover selected root-lock, ordering/limit, and compiled-query forms; specific tested relational shapes reject before issuing a SELECT where required. Arbitrary unmarked-predicate subqueries and global query filters remain uncharacterized. This is an experimental API, not a stable public contract or production locking guarantee. See [architecture](architecture.md), [internal mechanisms](internal-mechanisms.md), [query integration](query-integration.md), and the [PoC corpus](poc-plan.md).

## Implementation and proof requirements

1. Keep one .NET 10 runtime project and one test project. Record exact EF Core/Npgsql versions and the source baseline for provider internals with change evidence.
2. Compare native Npgsql behavior with each registered carrier. Use real PostgreSQL and two independent connections/transactions to observe lock conflicts, release on commit/rollback, and the absence of unintended locks. SQL text alone is insufficient.
3. Keep the `IQueryable<T>.ForUpdate()` marker non-no-op. If registration is missing, a normal unlocked `SELECT` must not succeed as if the request were fulfilled. The public entry point must not directly substitute a `TagWith` comment for the lock request.
4. Compare A (typed table annotation), B (query-translator tag), and C (query-expression-interceptor tag) against the same corpus. Keep all three as probes until maintainers select a carrier and supported shapes. Remove losing probes before production admission.
5. Prove alias/scope and fail-closed behavior for each admitted shape; test missing registration, replaced generator/translator, joins, nested queries, compiled-query reuse, cache variation and pooling. Explicitly reject unsupported shapes before returning data as locked.
6. Preserve Npgsql's configured SQL target as translation authority. Treat the connected PostgreSQL version and installed extensions as separate probe facts. Verify additive plugin behavior and service lifetimes.
7. Keep exact versions, test cases, SQL, provider outcomes, candidate revision and run results with change/evidence owners. Maintained documentation carries the resulting current contract and limitations.

## Production admission

- **GO:** one carrier proves target alias/scope, cache behavior, failure without registration, PostgreSQL locking semantics and compatibility with supported EF options. Maintainers approve its exact API and supported shapes.
- **NARROW:** admit only the proven root-entity subset and fail closed elsewhere.
- **HOLD:** no carrier proves fail-closed behavior and correct scope. Defer row locking or keep it experimental; a command interceptor must not rewrite SQL as a fallback.

The current repository already contains the minimum project and CI configuration. Local commands are in [CONTRIBUTING](../CONTRIBUTING.md). A packed-consumer smoke is required for package usability claims; it is not implied by build, tests or `dotnet pack`.

## Scope boundaries

Do not add a universal SQL operation base, a second DI framework, a capability registry, automatic server SQL-target switching, hidden transactions or retries. Keep file structure aligned with implemented responsibilities. Update the architecture and compatibility sections when maintainers admit a carrier or change the supported contract.
