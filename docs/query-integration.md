# PGEXT-Q — Query Integration internal design contract (candidate)

The source contains three experimental row-lock carriers selectable through `UsePostgreSqlExtensions(PocCarrier)`: typed table annotations (A), a query-translator tag (B), and an expression-interceptor tag (C). They remain comparison probes; no production carrier or supported-shape set has been selected. Bounded local PostgreSQL checks cover id-equality root locks, `OrderBy(...).Take(1)`, and synchronous/asynchronous compiled-query forms across A/B/C. Specific tested relational cases in Q05–Q12 reject before issuing a SELECT where required. Arbitrary unmarked-predicate subqueries and global query filters remain uncharacterized. This contract describes required behavior, while implementation details below identify current probes and remaining proof boundaries.

## 1. The central invariant

When application code requests a row lock through `IQueryable<T>`, one of **exactly two** outcomes is permitted:

1. EF Core/Npgsql generates a PostgreSQL query that locks **exactly the intended FROM occurrences** using the requested lock strength/wait policy under a real caller-controlled transaction; or
2. The query fails clearly, **before returning data as if it had been locked**.

Success with the original unmodified unlocked query is always incorrect. SQL text generation, query-cache reuse, non-tracking materialization and cancellation must not silently downgrade the locking request.

The current experimental scope accepts only one unambiguous mapped root entity table. Explicit multi-table targeting and general projections require demonstrated correctness; there is no entitlement to lock every JOIN table.

## 2. End-to-end mechanics

```
Client IQueryable<T>.ForUpdate()     (construct unambiguous marker method call)
  → EF expression pipeline
  → Translate method to immutable PgRowLockIntent
  → Bind exact semantic target occurrence(s) to relational FROM occurrences
  → Preserve intent through EF projection/pushdown/pruning/alias transformations
  → Validate final SELECT shape and unambiguous target(s)
  → Npgsql generator emits PostgreSQL `FOR UPDATE [OF ...]`
  → Command-time validation confirms active borrowed transaction and intact wiring
  → PostgreSQL executes; locks released on owner's COMMIT/ROLLBACK
```

The **intent is not an SQL string**, table alias name, `AsyncLocal` or provider-global flag. Any intermediate carrier (table annotation/tag) is an implementation choice, so the semantic record must remain expressible without exposing those carriers publicly.

### Query-entry contract

- `ForUpdate()` returns a new `IQueryable<T>` whose `Expression` contains a recognized method-call marker to our own method definition. **A direct implementation that simply calls `TagWith()` is disallowed**: without plugin registration EF could run the query unlocked.
- Marker args are *structural constants* / type-safe variants (strength, wait mode, target policy). They cannot be silently moved into ordinary SQL parameters: SQL keywords/aliases cannot be parameters. Dynamic lock mode inside one compiled query must be rejected or compiled as distinct query shapes based on a proven mechanism.
- Without plugin, the expression must **fail EF translation**, not become a no-op. Test unregistered and mismatched provider.
- Repeated identical lock request may be idempotent if proven; conflicting lock requests on the same semantic target are rejected unless precise precedence is consciously designed. Do **not** rely on last `TagWith` tag due to `ISet<string>`.
- User-supplied `TagWith` comments are *not* authorization to introduce locks. Internal reserved markers are not an external public protocol. If a tagging carrier is selected, PoC must prove collision/spoof handling and the additional fail-loud behavior when the generator is missing.

### Semantic intent (illustrative only)

```csharp
internal sealed record PgRowLockIntent(
    PgRowLockStrength Strength,            // Update | NoKeyUpdate | Share | KeyShare
    PgRowLockWait Wait,                    // Wait | NoWait | SkipLocked
    PgLockTargetPolicy Targets);           // RootEntityOnly, for initial candidate
```

**Not** a public `LockOperation` class and **not** an interface per enum. Later `Of<TEntity>` targeting requires binding exact occurrence(s) in joins, including self-joins. An `IEntityType` alone is not sufficient occurrence identity. The API must not allow arbitrary raw alias/SQL/table names as lock targets.

## 3. Exact EF Core 10 constraints and why each matters

| Source finding | Consequence |
|---|---|
| `SelectExpression` is `sealed` | Cannot derive a `PgLockSelectExpression` while preserving native composition. |
| `SelectExpression.WithAnnotations()` throws `UnreachableException` | Cannot attach lock intent directly through `SelectExpression.AddAnnotation()` in EF Core 10. |
| `TableExpression.WithAnnotations()` returns a new annotated table and `Clone()` preserves annotations | Possible carrier **only for table-attached information**, not proven for SELECT-wide PostgreSQL clause. |
| `TableExpression.Equals` / hash ignore added annotations | Possible expression equality/cache/copy hazard; regression test required. Do not assume annotation survives all compiler optimizations. |
| `SelectExpression.SetTables()` is `[EntityFrameworkInternal]` and demands mutable SELECT | Table rewriting must occur at the permitted stage; cannot patch immutable trees casually. |
| `SelectExpression.Tags` is `ISet<string>`, not ordered array | Duplicate/conflicting tags require explicit detection; no last-wins based on order. |
| `NpgsqlQueryableMethodTranslatingExpressionVisitor.CreateSubqueryVisitor()` constructs native visitor | Our subclass must override this to preserve translator behavior in nested queries; constructor signatures couple to Npgsql internal APIs. |
| Npgsql postprocessor adds `Unnest` and set-operation passes | Replacing postprocessor without preserving provider behavior is prohibited. Prefer avoiding this factory replacement. |
| `NpgsqlQuerySqlGenerator.VisitSelect()` visits nested SELECTs too | Generator must render clause in the **correct SELECT scope**, not every subquery containing an annotated table. |
| `ReplaceService` replaces a single EF internal service | Plugins replacing same factory can override each other; test final service graph, missing registration/late replacement and external internal provider. |

Primary files and links are in `internal-mechanisms.md` §8 and previous SQL Integration Research §10.

## 4. Implemented comparison carriers

| Candidate | Pipeline topology | Technical attraction | Specific failure risks and PoC conditions | Current status |
|---|---|---|---|---|
| **A: typed table annotation** | Npgsql QMT visitor plus SQL generator; carries intent on the root table occurrence | Structural carrier with no SQL text parsing | EF table annotation equality/hash, pushdown and alias/scope behavior require provider evidence | Implemented for comparison; selection pending |
| **B: translator tag** | Npgsql QMT visitor plus SQL generator; translates the marker into a reserved internal tag | Uses EF query-tag transport after recognizing the typed marker | Tag ordering, spoof/collision, cache behavior and generator replacement require evidence | Implemented for comparison; selection pending |
| **C: expression-interceptor tag** | Query expression interceptor plus SQL generator; rewrites the marker to a reserved internal tag | Retains the native Npgsql QMT visitor | Rewrite timing, cache behavior, scope and generator replacement require evidence | Implemented for comparison; selection pending |

For B/C, public `ForUpdate()` creates a typed marker expression. The installed translator or expression interceptor must process that marker before it becomes an internal tag; the public method does not directly call `TagWith`. The command interceptor validates the active transaction and carrier fulfillment marker. It does not rewrite command text or provide missing SQL behavior. No A/B/C winner is selected; production admission requires a maintainer decision after real-provider evidence.

## 5. Table occurrence and SQL scope rules

**Strong initial proposal for v1:** `ForUpdate()` acts on exactly one mapped, unambiguous root entity occurrence; SQL emits a fully quoted `OF <generated alias>` whenever alias resolution permits. If EF produces multiple table occurrences, a self-join, TPT/TPC fragments, nullable-side outer join or subquery scopes and root mapping cannot be established unequivocally, fail. Do not pick `select.Tables[0].Alias` merely because it is the first table.

For PostgreSQL, `FOR UPDATE` without `OF` applies to all tables in the SELECT. `OF` refers to `from_reference` aliases, not arbitrary EF entity names. The PostgreSQL doc says clauses applied at SELECT level are different from locking inside a sub-SELECT and excludes aggregate/grouping contexts. Thus correctness involves *scope* as well as final text.

**Initial admission matrix proposal (requires real proof):**

- **Must support to declare first useful v1:** unambiguous tracked and `AsNoTracking` entity root; `Where().ForUpdate().SingleAsync()`; `ForUpdate().Where()`; `OrderBy/Take` only after observing exact PostgreSQL semantics (including `OFFSET` locking behavior and READ COMMITTED sorting warning).
- **Must test; activate only with positive evidence:** INNER JOIN root-target, LEFT JOIN root-target, Select projection returning same mapped entity, nested subquery/pushdown, `Include`, `AsSplitQuery`, `EF.CompileQuery/AsyncQuery`, multiple target locks.
- **Fail loud unless positively admitted:** `DISTINCT`, `GROUP BY`, aggregate terminal `Count/Sum/Max`, `Union/Except/Intersect`, unknown subselect targets, self-join ambiguous root, `FromSql` raw execution, JOIN nullable-side `OF`, arbitrary unowned projection.
- **Contract nuance:** allowed `Any()` and `Count()` are not implied by being compilable. EF can transform one query into a scalar SELECT; if lock cannot target meaningful returned rows, reject rather than emit misleading SQL.

`FOR UPDATE` and `ORDER BY` at READ COMMITTED do **not** constitute a universal deterministic lock acquisition order; inserting it in a subquery can change row cardinality and scope. Preserve source-owner transaction requirements, do not promise implicit safe lock ordering.

## 6. Runtime preconditions and observability

- Caller owns transaction start/commit/rollback; no automatic transaction or retry.
- `IQueryable` may be constructed before a transaction opens, so transaction precondition belongs at **execution** (command interception or other provable gate), not only at `ForUpdate()` call time.
- A borrowed EF transaction via `UseTransaction` should be treated according to EF's actual `CurrentTransaction`; ambient `TransactionScope` cannot be assumed equivalent without PoC.
- The experimental generator emits paired request and fulfillment markers for command-time validation. The interceptor verifies both markers, the configured carrier's service wiring, and the current EF transaction on the command connection. **No command-text rewriting to add locks**. Do not rely on `AsyncLocal` state.
- Keep PostgreSQL native errors recognizable: `55P03` lock-not-available, `40P01` deadlock, `40001` serialization failure, query cancellation. These are not translation/configuration defects.
- `ToQueryString` and actual executed SQL should correspond for admitted shapes (subject to normal EF parameter/debug representations). Compare both.
- No arbitrary user table names, identifier SQL injection, passwords or sensitive parameter data in diagnostic markers/tags.

## 7. Registration / silent-omission threat analysis

| Fault injected | Required behavior |
|---|---|
| User calls `.ForUpdate()` without `UsePostgreSqlExtensions()` | LINQ translation error or explicit configuration error — never plain SELECT success |
| Our QMT visitor present but custom generator replaced by other package | Deterministic configuration/compilation/runtime failure; **never** unlocked SELECT success |
| Our generator present but request marker lost through EF projection/pushdown | Translation error on known locked request; not success |
| Two lock modes collide (e.g. UPDATE and SHARE) | Explicit conflict/ordered semantic contract; until proven, reject |
| User `TagWith` mimics reserved internal tag | No privilege/security bypass or unexpected locking; reject/ignore user's spoof under tested policy |
| Query compiled under one EF options target but reused with different service graph | Cache segregation or fail; no invalid cross-context reuse |
| Another plugin uses standard additive scalar translators | Retain it (`UseNodaTime` must still work) |
| `UseInternalServiceProvider` manually configures EF services | Explicit supported registration or fail-loud documented limitation |

The failure table is a **test specification**, not a claim that EF's current hooks automatically supply these protections.

## 8. Go/no-go criteria for final selection

One of A/B/C is acceptable **only** if: (a) it builds with the targeted EF/Npgsql packages, (b) preserves request shape through query transformations, (c) gives correct alias/scope locks with real PostgreSQL under concurrency, (d) rejects unsupported shapes and missing registration without silent unlocked queries, (e) remains correct under compiled-query reuse and DbContextFactory/Pool, (f) avoids unmaintainable provider service substitutions, and (g) coexists with Npgsql additive translators. Rank viable implementations by safety and maintainability, not by line count or one SQL string.

On any failure, do not mask it with `TagWith` textual fallback. Either narrow the officially supported shape set or postpone row locking while shipping independent advisory/clock functionality.

## 9. What this document does NOT fix yet

Stable `ForUpdate` overloads, typed multi-target selectors, precedence of multiple lock clauses, `FOR UPDATE` on TPT/TPC, runtime lock mode in `EF.CompileAsyncQuery`, PostgreSQL wait/timeout policy (no `SET LOCAL` until approved), full Npgsql 10.x compatibility, and the production service-composition contract remain open. Source APIs and current carrier registration are experimental and do not settle these decisions.
