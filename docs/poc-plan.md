# PGEXT-Q-PoC — Executable design-validation work package

This is the bounded experimental comparison plan. Its carrier alternatives and open questions describe the research scope. The [first-production contract](briefs/production-contract.md) owns the selected implementation and current admission requirements; [query gates](query-gates.md) describes current verification. Detailed comparison outcomes belong to [experimental evidence](experiments/query-carriers.md).

## Scope and order

The PoC compares three implementations of the same public `.ForUpdate()` marker: A typed table annotation, B query-translator tag, and C query-expression-interceptor tag. Keep all three as experimental comparison probes until maintainer review selects one. Before production admission, remove losing probes and retain only cases that protect admitted invariants. Distinguish PoC evidence from stable library acceptance.

### P0 — Dependencies and baseline

- Pin one exact *test* baseline pair of EF Core 10.x and `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x, record NuGet transitive graph. Package version published to consumers uses a documented supported range rather than test lockfile pins.
- Use the actual Npgsql provider; no invented EF Core tree clones. `net10.0` + SDK compatible with `global.json`. Unit vs PostgreSQL integration separated.
- Ordinary `UseNpgsql`, `UsePostgreSqlExtensions` registration, `DbContext`, `AddDbContextPool`, `AddDbContextFactory`, and optional `UseNodaTime` must be exercised.
- Store baseline raw SQL (`ToQueryString`) for unmodified native Npgsql; ensure feature OFF path produces exactly native semantics and does not replace services unnecessarily.
- Record effective provider SQL target and actual connected PostgreSQL version **separately**; no self-owned SQL target setting.

### P1 — Compare the three implemented carriers

| Candidate | Technical work | Specific proof |
|---|---|---|
| A `TableExpression` annotations | `IQueryable<T>` `Expression.Call` entry; derived `NpgsqlQueryableMethodTranslatingExpressionVisitor` + subclass-correct `CreateSubqueryVisitor`; immutable lock intent attached to mapped table occurrence; derived generator | Original `TableExpression`/JOIN behavior preserved; root target survives clone/remap/pruning; annotations not lost due to equality/cache or alias rewrite |
| B strict `TagWith` carrier | Same marker entry, derived QMT translates to **canonical literal tag** only after validation; derived generator reads tag and final tree | No direct TagWith fallback when registration missing; `ISet` ordering/dedup ignored; tags cannot leak into wrong subqueries; conflicts/spoof fail; no ambient fields |
| C query-expression interceptor + strict tag | Public marker retained in original tree; `IQueryExpressionInterceptor` rewrites to canonical tag at compilation; derived generator consumes marker | Same A/B behavioral suite, fewer provider service replacements; compiled query caching and parameter extraction don't invalidate marker; interceptor and generator wiring both required |

Do **not** implement custom sealed `SelectExpression` subclass or its unusable `AddAnnotation` path, and do not overwrite PostgreSQL SQL text via command interceptor.

### P2 — Query shape matrix (all candidates must face same corpus)

| ID | Query | Expected outcome / scope |
|---|---|---|
| Q01 | `db.Entities.Where(id).ForUpdate().SingleAsync()` | `FOR UPDATE OF <root_alias>`; exact one-row lock; root only |
| Q02 | `db.Entities.ForUpdate().Where(id).SingleAsync()` | Either proven equivalent to Q01 or explicit unsupported with rationale; no silent unlocked query |
| Q03 | `db.Entities.AsNoTracking().Where(id).ForShare()` | Correct `FOR SHARE OF ...`; no dependency on change tracking |
| Q04 | `db.Entities.Where(...).OrderBy(...).Take(1).ForUpdate()` | Correct scope/cardinality, no assumption ORDER BY guarantees acquisition ordering; bounded to PG semantics |
| Q05 | `join` root entity on other entity, `.ForUpdate()` | Root table occurrence only; `FOR UPDATE OF <root_alias>` after rename/remap; unknown root fails |
| Q06 | LEFT JOIN with nullable side | Never lock nullable side unless valid and explicit; otherwise fail with shape reason |
| Q07 | Self-join of one entity type | Must NOT guess alias by entity type or first table; fail until exact occurrence selection supported |
| Q08 | Projection into DTO/scalar after locking | Must lock same requested root row(s) only if carrier/scope survives; otherwise reject |
| Q09 | `Include`/`AsSplitQuery` | Scope of each SQL command known and validated; if uncertain, reject; multiple commands cannot silently execute unlocked |
| Q10 | subquery + `OrderBy`/`Take` pushdown | Lock in intended SELECT only, not duplicated or shifted to more rows |
| Q11 | `Distinct` / `GroupBy` / `CountAsync` / `SumAsync` | Explicit unsupported if row identity/locking semantics not provable |
| Q12 | `Union`/`Except`/`Intersect` | Explicit unsupported unless PostgreSQL syntax and target semantics proved in a constrained special case |
| Q13 | `.TagWith("user text")` + locked query | User tags preserved; ordering-independent internal carrier; reserved-prefix spoof not treated as permission |
| Q14 | two nested lock requests: identical + conflicting modes | Deterministic: idempotent same-intent or explicit reject; contradictory intents reject by default |
| Q15 | `EF.CompileQuery` and `EF.CompileAsyncQuery` | Mode/scope preserved; no cache collision between locked/unlocked queries |
| Q16 | dynamic/captured strength, wait mode or target | Structural SQL options cannot become ordinary SQL parameters; reject or prove cache-key segregation |
| Q17 | no registration / other provider / generator overwritten | Translation/configuration/runtime fail, **never** unlocked SELECT success |
| Q18 | ordinary query without lock request | Same SQL/result as native Npgsql; no global SQL semantic regression |

For every admitted case, prove both (a) generated SQL structurally has the correct `OF` occurrence(s) and (b) runtime locks exactly those rows in PostgreSQL. Unsupported cases should fail deterministically and before false success. Tests that only assert raw text are insufficient.

### P3 — PostgreSQL two-session behavior

1. Open two distinct connections and explicit transactions against the same real PostgreSQL instance; use one test entity table and second JOIN table.
2. Transaction 1 executes the **generated** locking query. Transaction 2 attempts a conflicting UPDATE or `FOR UPDATE NOWAIT` on the target row; expect `55P03` (lock not available) or blocked operation within bounded cancellation/deadline.
3. Transaction 2 simultaneously attempts to modify non-target JOIN row; it must remain unblocked for root-only locking (subject to normal FK constraints, which must not cause false test positives).
4. Test `FOR SHARE` / `FOR KEY SHARE` compatibility matrix as separate cases, not merely `FOR UPDATE`. Test `NOWAIT` and `SKIP LOCKED` with two rows and known holder locks. Test release after both commit and rollback.
5. Test borrowed EF transaction (`UseTransaction`) and missing transaction. Query creation before transaction opening is valid; query execution without eligible transaction must fail. `TransactionScope` is not assumed supported without real proof.
6. Run under READ COMMITTED. Validate `LIMIT/OFFSET` row selection/lock scope and any key modifications under concurrent writers. Do not derive a global deterministic lock order guarantee from `ORDER BY`.
7. Failure injections: cancel waiting lock; kill connection; serialization/deadlock errors; ensure errors classified correctly rather than dropped or retried automatically.

Prefer observing *blocking outcomes* rather than relying solely on `pg_locks`, since tuple-lock visibility may not match naive expectations. Bound all concurrency tests so they cannot hang CI indefinitely.

### P4 — Registration, provider lifecycle, cache/multi-context

- Register twice (idempotent), not registered (fail closed), accidental different provider (fail closed), replace generator after our registration (no silent lock loss), NodaTime coexistence, external EF internal service provider behavior.
- Same model/options, differing `ForUpdate` strength; independent query forms and compiled queries; same query executed under different `DbContextPool` instances. No cross-query global/`AsyncLocal` residual lock state.
- Two `DbContext` types and data sources with distinct target versions/options. A provider SQL-target mismatch must be detected, but **actual** PostgreSQL server version never mutates cached translation.
- Test exact Npgsql patch range; at least baseline min and tested latest on supported 10.x. SQL generator subclass constructors and factory APIs are internal and may change even in patch releases.

### P5 — Independent mechanisms smoke (no forced Query dependencies)

| Mechanism | Minimal proof | Should be absent |
|---|---|---|
| Scalar clock | Confirm no suitable Npgsql builtin; if needed `DbFunction` or additive plugin translates `clock_timestamp()`; compare with `now()` across a held transaction | New global query-factory replacement |
| Advisory locks | Prefer `DatabaseFacade.ExecuteSqlAsync` parameterized `SELECT pg_advisory_xact_lock(hashtextextended({key},0))`; verify explicit borrowed transaction, 2-session blocking, server-side key, commit auto-release | Custom lock manager, ambient scope, hidden retry/new transaction |
| Insert-ignore | Typed one-table mapping, generated identifiers from EF metadata, parameters and value converters, atomic affected-row semantics of `ON CONFLICT DO NOTHING`; 2-session competing insert | Broad Upsert DSL, silent tracking/SaveChanges effects, check-then-insert |
| Capability startup check | Npgsql configured SQL target vs actual server; installed required extension only; validation after host migrations before work | Mandatory per-LINQ probe, duplicate manual version setting, implicit CREATE EXTENSION |

For `DatabaseFacade.ExecuteSqlAsync`, EF Core explicitly documents **no implicit transaction** and **no implicit execution strategy/retry**. Leverage this property rather than recreating plumbing. Proof required for `SELECT` of `void` returning lock function when called through ExecuteSqlAsync; should not assume every provider reports a meaningful row count. If direct provider command proves necessary, isolate it locally in Commands/AdvisoryLocks (not shared command framework).

### P6 — Go/no-go and useful output

**Required artifact after real execution:** a concise review of A/B/C with exact package versions and commits, compiler errors and effective EF DI graph, SQL corpus outcomes, PostgreSQL two-session proofs, explicit rejected query shapes, and selected carrier/registration path. Promote only admitted functionality to public API; exact method names/signatures, `FOR UPDATE OF` targets and runtime preconditions are approved **after** evidence.

- **GO:** clear selection with strong safety and reasonable support cost, no silent unlock, provider compatibility matrix passed; minimal implementation can form one independent library implementation Slice.
- **NARROW:** only root entity locking works; ship/document a narrower API if it is valuable and fail-closed on rest.
- **HOLD:** all candidates rely on incompatible Npgsql internals or cannot preserve intent/scope. Release advisory/clock first and retain row locking as a separate workstream; do not use string interceptor rewrite as an emergency workaround.

## No false evidence statement

Source-level feasibility observations do not establish a compiled program or PostgreSQL concurrency result. Execute restore/build/provider tests before admitting an API or query shape.
