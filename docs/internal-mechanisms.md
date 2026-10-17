# EntityFrameworkCore.PostgreSQL.Extensions — Internal Mechanisms Architecture

The source implements experimental representatives of the four mechanisms below. The architecture and constraints remain the design baseline; these implementations have not been admitted as stable public behavior. Bounded local PostgreSQL checks exercise selected locking, DML, and scalar-function forms. Arbitrary unmarked-predicate subqueries and global query filters remain uncharacterized; detailed verification results belong with change evidence.


## 0. Решения и границы

### Architectural baseline

1. Independent EF Core/Npgsql PostgreSQL extension package; no application/domain knowledge.
2. One NuGet package and one main library assembly, initial `net10.0` + EF Core/Npgsql 10, separate Git repository, public API shaped like native EF Core. No universal `IPostgreSqlOperation`.
3. Four **technical integration mechanisms** for v1: Query/SQL-tree, scalar SQL expression, transaction-scoped command execution, metadata-aware DML. Their count is NOT a closed future taxonomy.
4. No hidden transactions, retries, domain lifecycle, owner-specific SQL, authorization, unsafe SQL string modification or silent loss of requested semantics.
5. Npgsql effective PostgreSQL SQL target is the sole target for translation; actual connected-server version and installed PostgreSQL extensions are independently discovered for runtime compatibility validation. No second manual version setting in our package.
6. The library provides caller-invoked capability discovery. Any strict startup/readiness gate belongs to host/deployment integration and remains outside the implemented library lifecycle.

### Open implementation and admission decisions

- Exact form of Query Integration carrier, service replacement topology, command-time transaction marker and supported LINQ shape matrix.
- Whether `IQueryExpressionInterceptor` plus *strict internal tagging* can safely remove one provider service replacement.
- Which PostgreSQL operations can be implemented solely via native EF `DatabaseFacade.ExecuteSqlAsync`, avoiding custom ADO.NET command layers.
- Typed insert-ignore command shape and scope of EF metadata/value-converter support.
- Source-level adapters and visibility boundaries; these are implementation principles, not mandatory public interfaces.

## 1. Dependency and API topology

```
End-user APIs (one namespace)
  IQueryable<T>.ForUpdate / ForShare ... ───────► Query Integration
  DatabaseClock.ClockTimestamp() ──────────────► Scalar Expression Integration
  DatabaseFacade.*AdvisoryLockAsync ───────────► Command Execution
  DbContext.*InsertIgnoreAsync ────────────────► DML Construction
                                               │
          ┌────────────────────────────────────┘
          ▼
Shared *thin* technical facilities (not a service-oriented runtime):
  Provider registration + Npgsql target observation
  Safe diagnostics/error boundaries
  Caller-invoked capability probe (host startup/readiness lifecycle remains outside the library)
  Physical EF metadata + SQL quoting tools only where truly reused

  Query-specific : adapted Npgsql QMT visitor / SQL generator / intent carrier
  Scalar-specific: standard additive translator plugin OR native DbFunction
  Command-specific: native DatabaseFacade EF execution, borrowed transaction
  DML-specific: narrow EF metadata-based INSERT generator & execution
```

**What is shared, what is not:** shared registration and compatibility **rules**, not universal polymorphic operation contracts, shared SQL AST, generic compiler, SQL text parser, base classes per feature or instrumentation on every invocation. A component should become shared only after a second real consumer requires exactly the same semantics. Component directories are for cohesion, not an obligation to create one class per abstract role.

### Minimal project structure (conceptual, **not** mandated physical files)

```
src/EntityFrameworkCore.PostgreSQL.Extensions/
  Query/Locking/                 request semantics + translator + SQL generator adapter
  Functions/DatabaseClock/      `DbFunction` or scalar translator
  Commands/AdvisoryLocks/       EF borrowed-transaction helper
  Dml/InsertIgnore/             EF metadata mapping + parameterized SQL
  Integration/                  configuration, provider-version seam, diagnostics
  Compatibility/                discovery/probe/validation (only when required)
```

Do not create `Abstractions.csproj`, internal mediator, `Operation<T>`, SQL node registry, method-per-file hierarchy or Plugin SDK. Keep external consumers independent from these internal types.

## 2. Mechanism contracts (implementation-design level)

| Mechanism | Input | Output | Technical ownership and allowed dependencies | Failure / lifetime | Registration |
|---|---|---|---|---|---|
| **Q — Whole Query** | Immutable lock intent on a LINQ query and its target table occurrence | Npgsql `SELECT ... FOR <mode> [OF alias] [NOWAIT/SKIP LOCKED]` | EF query visitor and Npgsql SQL generator | Reject unsupported shapes; validate transaction at execution; lock lifetime belongs to caller transaction | Three carrier probes are configurable; selection remains open |
| **S — Scalar expression** | Mapped database function with CLR arguments | Typed SQL function expression | EF `DbFunction` mapping; no whole-query replacement | Client invocation throws; no client fallback | Model configuration through `MapClockTimestamp` |
| **C — Command execution** | `DatabaseFacade`, cancellation token, existing transaction, typed parameters | Transaction-scoped PostgreSQL primitive | EF/provider execution API | Caller owns connection, transaction, retry and commit; no hidden transaction or retry | Experimental advisory-lock helper; no separate registration |
| **D — Typed DML** | `DbContext`, mapped entity, typed values and explicit conflict target | Atomic insert or ignored-conflict outcome | EF metadata and parameterized PostgreSQL `INSERT ... ON CONFLICT` | Unsupported mapping fails; no check-then-insert; tracking/transaction effects are explicit | Experimental insert-ignore helper; no separate registration |

### Hypothetical *internal* semantic data shapes (not required interface/class names)

```csharp
internal enum PgRowLockStrength { Update, NoKeyUpdate, Share, KeyShare }
internal enum PgRowLockWait { Wait, NoWait, SkipLocked }

// Only describes behavior. Target is a logical SQL FROM occurrence, not a raw alias.
internal sealed record PgRowLockIntent(
    PgRowLockStrength Strength,
    PgRowLockWait Wait,
    PgLockTargetPolicy TargetPolicy);

internal enum PgLockTargetPolicy { RootEntityOnly } // initial candidate; explicit targets later
```

`RootEntityOnly` is deliberately narrow. A CLR entity type by itself cannot distinguish a self-JOIN's table occurrences. No string table/alias input in v1. If root resolution is ambiguous in final SQL tree, reject. `PgRowLockIntent` is a **conceptual internal value**, not a request to implement a common `IOperation` interface. `Wait` and lock mode are SQL-structure choices, not database parameters: compiled-query caching must distinguish variants or reject dynamic mode values.

## 3. Registration architecture and EF service lifetimes

- Adopt one opt-in public configuration surface only for features requiring EF provider services; pure command helpers should not require it.
- The query probe uses `IDbContextOptionsExtension`; its service-provider cache information includes the selected carrier. Future translation options must also participate in cache identity so enabled features do not leak across distinct DbContext options/service providers.
- A scalar translator plugin is **additive**; a query-wide translator factory or SQL generator factory is a **single registered provider service**, not additive. Replacing services of Npgsql is scoped technical debt and must be tested across Npgsql patch changes and other plugins.
- `ReplaceService` works when EF manages its internal provider; externally supplied internal provider (`UseInternalServiceProvider`) requires an explicit supported registration path or clear unsupported diagnostic. See EF `DbContextOptionsBuilder.ReplaceService` documentation.
- Repeated `UsePostgreSqlExtensions` must be idempotent; no duplicate interceptors or terminal service chains. A second provider factory replacement by an unrelated plugin must not silently drop the locking clause. Test final graph and, if unable to validate globally, provide a fail-closed runtime/translation invariant.
- `AddDbContext`, `AddDbContextFactory`, `AddDbContextPool`, multiple different data sources/options, design-time migrations and NodaTime coexistence need independently executable checks.
- Follow the Npgsql **configured** `PostgresVersion` for SQL features. Actual connected server version is a probe fact only; never mutate a singleton translator after first query to track failover/replicas.

## 4. Library-wide fail-loud contract

Distinguish at least these **diagnostic categories** (exact CLR exception types/names deferred):

1. Configuration: provider not Npgsql; missing required registration; provider factory conflict; inconsistent version target.
2. Translation/shape: unsupported `IQueryable` chain, wrong scope/alias, invalid lock target, unsupported DML mapping; **never** silently execute an unlocked query.
3. Runtime preconditions: missing/unsupported existing transaction for requested lock/command, unavailable required PostgreSQL feature, unsupported actual version.
4. PostgreSQL runtime outcomes: `NOWAIT` lock unavailable / server deadlock / cancellation / serialization failure are not misreported as setup/configuration; preserve `PostgresException.SqlState` for consumer handling unless a **purposeful narrow** exception projection is proven useful.

Runtime capability discovery is database-scoped; startup validation is a distinct host-owned gate, not `DbContext` initialization or EF LINQ query compilation. Don't poll PG on every query. Diagnostics must not leak credentials, full connection strings or data values.

## 5. Feature admission (use existing APIs first)

Before building a feature, document:

1. Concrete PostgreSQL semantic need and affected EF API.
2. Does current EF Core/Npgsql already provide it (`DbFunction`, translator, `ExecuteUpdate`, `ExecuteDelete`, `SqlQuery`, raw SQL APIs)? If yes, prefer the built-in.
3. Cheapest hook that **preserves** semantics; use deeper provider internals only when necessary.
4. Bounded supported/unsupported shapes, concurrency or PostgreSQL-version assertions and diagnostic outcomes.
5. Whether an existing mechanism truly supports it. A fifth mechanism is legitimate with evidence; four are not a fixed ontology.

This is a short developer checklist, **not** a new source registry, persisted capability descriptors or an AI-specific gate engine.

## 6. Compatibility, tests, publication

- One `net10.0` package; tested Npgsql/EF 10 major and stated PG versions. Exact patch min and supported PG range determined by real test matrix, not inferred from signature compilation.
- Query compiler and PostgreSQL concurrency need different evidence: `ToQueryString()`/SQL AST tests + two-session lock behavior tests. Both required.
- Single-candidate compiled query, parameterized data filters, no cross-query `AsyncLocal` residual state; Npgsql SQL target differences tested independently.
- Scalars/commands/DML get focused positive+negative tests; don't require universal TCK framework, full all-versions Cartesian matrix or independent analyzer project.
- Package shipped with XML docs, SourceLink/symbols, README examples, SemVer + package validation, small compatibility page. Library own CI + self-contained contribution guide; architecture/API admission follows evidence and maintainer review within this repository.

## 7. Architectural recommendations from this round

| Decision ID | Recommendation | Status |
|---|---|---|
| MECH-01 | Four **integration mechanisms**, each with native EF-facing API and no shared `IOperation` | **Owner-approved architectural foundation**; concrete implementations gated by PoC |
| MECH-02 | One main assembly / NuGet, folders not microprojects | **Owner-accepted baseline** |
| MECH-03 | One opt-in provider registration when translation requires it, pure helpers no mandatory registration | Experimental query registration exists; stable carrier and supported composition remain open |
| MECH-04 | SQL generation from structural SQL tree; no raw command-text rewriting for row locks | **Owner-accepted direction** |
| MECH-05 | No manual PostgreSQL version duplication; Npgsql effective SQL target authoritative; actual server probed separately | **Owner-accepted direction** |
| MECH-06 | Query locking root-only scope initially; typed targeting later only after PoC | **Candidate; requires PoC/owner review** |
| MECH-07 | Standard EF `ExecuteSqlAsync` preferred for advisory lock if function semantics are proven | **Candidate; requires real-PG proof** |
| MECH-08 | Dedicated narrow INSERT-ignore DML adapter, not generic UPSERT/SQL AST | Experimental adapter exists; mapping and converter support require provider evidence |
| MECH-09 | Do not replace Npgsql postprocessor until a minimal PoC proves it necessary | **Approved minimization principle**, technical feasibility unproven |
| MECH-10 | Verify supported EF/Npgsql major/patch behavior at release + guard service replacement conflicts | **Candidate** |

## 8. Reference source baselines

Primary source URLs and exact seams:

- EF Core 10 `TableExpressionBase.AddAnnotation`, `TableExpression.WithAnnotations`, `SelectExpression.WithAnnotations` (throws), `SelectExpression` `sealed`/`Tags=ISet<string>`: https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore.Relational/Query/SqlExpressions/SelectExpression.cs ; https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore.Relational/Query/SqlExpressions/TableExpression.cs
- EF Core 10 query visitor, query interceptor and SQL generator: https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore/Query/QueryableMethodTranslatingExpressionVisitor.cs ; https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore/Diagnostics/IQueryExpressionInterceptor.cs ; https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore.Relational/Query/QuerySqlGenerator.cs
- Npgsql 10 `NpgsqlQueryableMethodTranslatingExpressionVisitor`, `NpgsqlQuerySqlGenerator`, `NpgsqlQueryTranslationPostprocessor`: https://github.com/npgsql/efcore.pg/blob/v10.0.0/src/EFCore.PG/Query/Internal/NpgsqlQueryableMethodTranslatingExpressionVisitor.cs ; https://github.com/npgsql/efcore.pg/blob/v10.0.0/src/EFCore.PG/Query/Internal/NpgsqlQuerySqlGenerator.cs ; https://github.com/npgsql/efcore.pg/blob/v10.0.0/src/EFCore.PG/Query/Internal/NpgsqlQueryTranslationPostprocessor.cs
- EF plugin guidance: https://learn.microsoft.com/en-us/ef/core/miscellaneous/plugins
- PostgreSQL SELECT locking: https://www.postgresql.org/docs/18/sql-select.html
- EF transaction-aware raw commands: https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.relationaldatabasefacadeextensions.executesqlrawasync?view=efcore-10.0
- Prior research lists NodaTime, mnbuhl, PandaTech, Thinktecture and FlexLabs source comparisons and primary links. Third-party code was consulted, **not copied**.
