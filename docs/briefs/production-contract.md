# First-production contract

**Status: approved implementation target.** This contract defines the first production implementation. Implementation admission, compatibility and package-consumer proof require the evidence below; approval alone establishes none of those results. [Architecture](../architecture.md) owns the general technical boundaries; [query integration](../query-integration.md) owns query correctness; [production brief](PGEXT-PRODUCTION.md) owns the implementation and verification outcome. Detailed research observations remain in [experimental evidence](../experiments/query-carriers.md).

## 1. Package boundary

One `net10.0` runtime assembly/package and namespace `EntityFrameworkCore.PostgreSQL.Extensions`. The four core capabilities are row locks, transaction-scoped advisory locks, atomic single-row insert-ignore and PostgreSQL clock mapping. Native EF/Npgsql behavior is retained wherever sufficient. Read-only transaction and capability-check helpers are bounded technical support described below.

The library accepts no application permissions, business revision policy, recovery outcome or retry policy. It creates no transaction, commits nothing for the caller and installs no database extension. No generic repository, command dispatcher, SQL AST framework, host lifecycle or additional runtime package is proposed.

## 2. Query implementation and registration

Use an expression interceptor to validate a typed lock marker and carry immutable intent through a reserved internal tag; generate the lock structurally in the final SELECT. Retain Npgsql's native query translator. Replace only `IQuerySqlGeneratorFactory`; a command interceptor validates fulfillment, service composition and the current transaction without modifying SQL. The annotation/translator comparison carriers and the public `PocCarrier` switch have no production runtime path.

Registration:

```csharp
options.UseNpgsql(connectionString,
    npgsql => npgsql.UsePostgreSqlExtensions());
```

`UsePostgreSqlExtensions(this NpgsqlDbContextOptionsBuilder)` returns the same builder. Use this parameterless registration for row-lock queries. There is no public carrier selector or compatibility alias for the experimental comparison paths. Commands, typed DML and the clock helpers retain their natural EF receivers; their admission requires the verification below.

Repeated registration is idempotent. A competing query-generator/translator replacement before or after registration fails explicitly; last-registration-wins behavior is forbidden. Additive native plugins such as NodaTime must remain functional. `AddDbContext`, factory and pooled configurations require separate composition tests. Manually assembled `UseInternalServiceProvider` configurations are unsupported and must fail clearly. Commands, DML and clock mapping require no query-plugin registration unless a row-lock query is used.

## 3. Row-lock API and admissible queries

Signatures, all in the public namespace:

```csharp
IQueryable<T> ForUpdate<T>(this IQueryable<T> source);
IQueryable<T> ForUpdate<T>(this IQueryable<T> source, LockWait wait);
IQueryable<T> ForShare<T>(this IQueryable<T> source);
IQueryable<T> ForShare<T>(this IQueryable<T> source, LockWait wait);
IQueryable<T> WithRowLock<T>(this IQueryable<T> source,
    LockStrength strength, LockWait wait);
```

`LockStrength`: `Update`, `NoKeyUpdate`, `Share`, `KeyShare`. `LockWait`: `Wait`, `NoWait`, `SkipLocked`. Parameterless calls use `Wait`. Strength/wait are structural constants, including in compiled queries; a runtime parameter selecting SQL keywords within one compiled query is rejected. Identical and conflicting repeated lock requests are both rejected. There is exactly one lock request and one unambiguous root table occurrence.

The first implementation target is the following closed set. An allowed operator name alone does not admit arbitrary predicates, mappings or compositions.

| Form | Admitted target and proof boundary |
|---|---|
| Mapped entity root | One directly mapped table, one entity occurrence, tracked or `AsNoTracking`; materialize the entity itself |
| Predicates | Equality of directly mapped scalar root properties to constants/parameters; conjunctions of those equalities; for C# numeric promotion, built-in equality admits the methodless, non-lifted `Convert` from `short` to `int` around exactly one direct mapped root member and, when present, the same conversion around a short constant/parameter on the value side. Validate the conversion as a pair and retain it in the expression. Other casts, computed members and method conversions remain rejected; no navigation traversal, subquery or client evaluation. Parameter binding remains native: an integer value outside a mapped `smallint` range can raise the same provider overflow as an unmarked query. Nullable-key cases and value converters need their explicit mapping/null/converter fixtures |
| Identity | Primary, natural and composite property predicates are eligible under that predicate rule. Natural/composite/converted consumer keys require additional real-provider proof; the id-equality PoC does not establish them |
| Marker placement | Before or after admitted `Where`; preserved target occurrence and lock scope required |
| Single entity terminal | `Single`, `SingleOrDefault`, `First`, `FirstOrDefault` and async equivalents; preserve native zero/multiple-result semantics and observe the actual generated limits |
| Bounded selection | `OrderBy`/descending and `ThenBy`/descending over mapped scalar root columns, followed by `Take(1)`; no OFFSET. Only admitted predicate forms may be combined with it; each additional composition needs its own execution oracle |
| Enumeration / compiled forms | Sync/async entity enumeration of admitted roots, `EF.CompileQuery` and `EF.CompileAsyncQuery` with constant lock options; separate cache, transaction and reuse proof |
| User query tags | Preserve ordinary constant tags; reject reserved-protocol impersonation, multiline spoofing and dynamic lock-query tags |

Reject before issuing the requested SELECT: JOIN/LEFT JOIN/self-join, DTO/scalar or identity `Select` projections, `Include`/split query, global query filters, navigation predicates, predicate subqueries, nested locking/pushdown, raw SQL roots, inheritance/table splitting, multiple lock requests, `Skip`, arbitrary `Take(n)`, `Distinct`, `GroupBy`, aggregates, `Any`/`Count`, set operations and unknown shapes. Validate both the original expression/model and the final relational tree; a root-looking final SELECT alone is insufficient. Untested mappings remain unadmitted. A shape outside the set must fail rather than return unlocked data.

Ordered multirow locks over a bounded array/key set are outside this initial target. A consumer needing that protocol requires an approved scope amendment and real-provider order/cardinality/target proof before migration. Locking a complete owner-local entity before projecting its values in memory is a possible consumer adaptation; it grants no access to another owner's entity and establishes no general projection-lock support.

Acquire the lock on the exact generated root alias. Lock strength and wait behavior retain PostgreSQL semantics. `ORDER BY` does not establish a general lock-acquisition order or post-wait ranking guarantee at READ COMMITTED. `SkipLocked` can omit occupied rows and provides no completeness/fairness guarantee. `Single` can lock inspected rows before detecting multiplicity; unique predicates and owner transaction design remain the caller's responsibility.

Query construction may precede transaction creation. Actual execution requires the current explicit EF transaction on the same command connection, including a correctly borrowed transaction through native EF enlistment. Ambient `TransactionScope` alone is unsupported. Missing registration/transaction, a replaced service, cache mismatch or unsupported shape must fail explicitly. Ordinary unmarked queries preserve native SQL/results. Cancellation and native provider exceptions propagate, including EF's native wrappers; the library neither retries nor classifies a domain outcome.

## 4. Advisory-lock contract

Retain these `DatabaseFacade` extension overloads, returning `Task`:

```csharp
AcquireTransactionAdvisoryLockAsync(string key, CancellationToken ct = default);
AcquireTransactionAdvisoryLockAsync(long key, CancellationToken ct = default);
AcquireTransactionAdvisoryLockAsync(int key1, int key2,
    CancellationToken ct = default);
```

Each uses the current explicit EF transaction/connection. Text uses PostgreSQL `hashtextextended(key, 0)` with `pg_advisory_xact_lock`; the library never substitutes a .NET hash. The bigint and two-int key spaces follow native PostgreSQL separation; the text overload uses the bigint space and can collide with an explicitly chosen bigint key. Text hashing is not collision-free. Callers choose a consistent key protocol; coordination applies to participants acquiring that lock. A competing operation may wait; cancellation propagates, and PostgreSQL releases the lock on transaction completion. No session-scoped fallback, implicit transaction, try-lock API or automatic retry is included.

## 5. Single-row insert-ignore contract

```csharp
Task<bool> InsertIgnoreAsync<TEntity>(this DbContext context,
    TEntity entity, Expression<Func<TEntity, object?>> conflictTarget,
    CancellationToken cancellationToken = default)
    where TEntity : class;
```

Insert one row; return `true` for one inserted row and `false` for zero rows due to the selected conflict. Use a mapped primary/alternate key or unfiltered unique index, selected by one direct property or an anonymous composite key. Build identifiers from EF metadata with provider quoting and values as parameters using their type mappings/converters. Other constraint violations propagate. Native nullable-unique semantics are preserved; NULL does not become application-level equality.

The admitted mapping target has one table, CLR scalar properties and explicitly verified value/default/converter mappings. Reject views, inheritance/table fragments, shadow/complex properties, partial/expression unique targets, duplicate target properties, unsupported generation and ambiguous shared-table mappings before opening a connection. Client-side key generators and SaveChanges interceptors are not executed by this method.

Use EF's configured sentinel as the omitted-value signal for a store-generated/defaulted property. An explicit NULL is expressible only where it is distinct from that omission signal. A nullable default with NULL as its sentinel cannot express explicit NULL through this entity API; do not claim both intents are supported. Unsupported or ambiguous mappings require explicit rejection or a separately approved contract extension. Preserve provider defaults and allowed explicit values according to that declared mapping contract.

This operation does not attach the entity, modify existing tracking state, call `SaveChanges`, return generated columns or assign generated values to the supplied instance. Borrow an existing caller transaction; without one, execute one statement with native PostgreSQL autocommit and no hidden `BeginTransaction`. Broader atomic work requires the caller's explicit transaction. No check-then-insert, `DO UPDATE`, bulk insertion or general RETURNING API is included.

## 6. Clock and technical support

Retain `ModelBuilder.MapClockTimestamp()` returning `ModelBuilder`, and `DateTime DatabaseClock.ClockTimestamp()` translated to `pg_catalog.clock_timestamp()`. Direct client invocation throws. UTC/microsecond mapping requires provider proof. PostgreSQL transaction time (`now()`) retains its distinct native meaning; there is no client-clock fallback. The mapping alone does not guarantee one evaluation of a volatile function or support for arbitrary update/CTE expressions. If several fields require one time basis, the caller must sample once or use a separately proven single-evaluation shape. Whether sampling is valid for a specific mutation predicate remains with its semantic owner.

`Task<DateTime> ReadClockTimestampAsync(this DatabaseFacade database, CancellationToken cancellationToken = default)` performs one native server clock read, returns one UTC value at PostgreSQL precision, borrows the current transaction when present and balances only connection ownership acquired by the call. Without a transaction, a native single-statement read is allowed; no transaction is created. The caller reuses the returned value explicitly. The helper requires real-provider and packed-consumer proof. It supplies no TTL/interval/overflow policy and does not prove equivalence to a compound server-side lease statement. Predicate and `ExecuteUpdate` clock translation, `DateTimeOffset` conversions, shared-sample server arithmetic and overflow behavior need separate proofs before those consumer uses are admitted.

Retain `Task DatabaseFacade.SetTransactionReadOnlyAsync(CancellationToken ct = default)` only as a native command under an existing transaction. PostgreSQL enforces command timing and write restrictions. It changes no session default, restores no aborted transaction and chooses no isolation level. It requires its own packed-consumer proof.

Retain the caller-invoked read-only `PostgreSqlCapabilityProbe.CheckAsync(DbContext, IEnumerable<PostgreSqlExtensionRequirement>, CancellationToken)` returning `Task<PostgreSqlCapabilityReport>`. No DI/startup hook is required. Requirements have `Name` and optional exact `RequiredVersion`; installed facts have `Name` and `Version`. Report fields remain `Outcome`, `SqlTargetVersion`, `ActualServerVersion`, `InstalledExtensions`, `UnmetRequirements`, optional `FailureType`. Outcome is `Compatible`, `Incompatible` or `Indeterminate`; it concerns the supplied requirements on one observed database. Cancellation propagates. It does not certify a deployment, other roles/replicas or all query capabilities.

The effective Npgsql SQL target governs SQL generation. The actual server and installed extensions are separate observations. Compare only declared required extensions; unused absent extensions are irrelevant. An unreadable database yields `Indeterminate`, a mismatch yields `Incompatible`; neither establishes readiness. The host chooses its readiness boundary and handles non-compatible results. The probe changes no target and installs no extension. Auto-target selection, per-query probing and host lifecycle management are excluded.

## 7. Initial compatibility policy

| Axis | Initial target for production evidence |
|---|---|
| Runtime | One `net10.0` assembly; .NET 10 |
| SDK | Existing stable `10.0.100` request with `latestFeature`; record resolved SDK. Initial reference resolution is `10.0.401` |
| Packages | EF Core/Relational `10.0.12`, Npgsql EF provider `10.0.3`, Npgsql `10.0.3`; coexistence proof with Npgsql NodaTime `10.0.3` |
| Database | PostgreSQL `16.14` reference server; no server patch/major range is inferred |
| SQL-generation target | Existing Npgsql target `14.0` against that server; the separate `15.0` check is a context-isolation probe, not server-14/15 support |
| Package identity/release | Actual ID ownership, release version/feed and publication permission remain release decisions |

Test these exact packages first. Minimum declared dependencies must match tested versions; the support line is EF/Npgsql major 10 because the query integration uses provider internals. Do not advertise other 10.x patches or major 11 as supported from this evidence. NuGet dependency intervals and major bounds must reflect the adopted support policy and be inspected in the actual package; downstream consumers choose patches within those intervals. Expanding declared support requires additional package-consumer/provider evidence and a documented support amendment. NativeAOT, trimming, extra TFMs and all-version matrices are excluded.

## 8. Admission evidence and decisions

The selected target comprises the expression-interceptor carrier, public API/registration, admitted/rejected shapes and initial feature/compatibility scope above. Each implemented guarantee requires current evidence. Any required narrower scope or semantic change must amend this contract before a consumer relies on it.

Production admission requires:

1. Build/unit and real PostgreSQL checks for every admitted mechanism, including natural/composite/converted keys, terminal/ordering compositions and rejected model/global-filter/predicate shapes.
2. Two-session target/non-target locks, commit/rollback release, strength/wait modes, cancellation/native errors, borrowed transactions and missing/wrong transaction controls. Rejected-shape controls must run in a valid transaction and assert the intended cause and zero issued commands.
3. Compiled/cache/context-factory/pooling and prior/late service-replacement faults; ordinary native queries and additive plugin behavior remain intact.
4. Atomic insert races, selected vs unrelated constraints, defaults/explicit NULL/converters/generated values/tracking; volatile vs transaction time and each claimed single-evaluation use; read-only/probe outcome and cancellation controls.
5. Pack inspection and a fresh isolated neutral application that installs the exact `.nupkg` from a local feed, with no project/source reference, then runs positive and fail-closed cases for all included features on real PostgreSQL. Reusable packed-consumer composition must cover the supported registration forms.
6. Independent substantive review, coherent maintained documentation and successful required CI runs. A local PoC run or workflow file alone does not establish those outcomes.

Public repository access, local pack construction, isolated consumption and NuGet publication are distinct results. Publishing requires its own explicit authorization and actual account rights. Research evidence and contract approval cannot substitute for production or downstream-owner acceptance.
