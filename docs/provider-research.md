# EntityFrameworkCore.PostgreSQL.Extensions — SQL Integration Architecture Research

This source-level research records EF Core 10 and Npgsql 10 implementation seams. It informs the current [query integration contract](query-integration.md), but does not select a production carrier or supersede executable PoC evidence. Npgsql's configured SQL target governs translation; the connected server version and installed extensions are separate runtime facts. The library remains independent of application and business semantics.

## 1. Задача и граница исследования

Выбрать **внутренние способы интеграции с EF Core 10 / Npgsql 10**, которые одновременно сохраняют естественный public API EF Core, потенциально широкий PostgreSQL SQL language, typed metadata (без физического SQL в прикладном коде), строгий fail-loud, composability, provider compatibility и низкую стоимость сопровождения. Не следует заранее предполагать один hook для всех семейств возможностей. Выбор касается технической **трансляции и исполнения**: нет решения создавать собственный ORM/SQL AST/универсальный DML DSL.

**Исследована именно source-level реализация** EF Core tag `v10.0.0` и Npgsql tag `v10.0.0`, spot-check на `v10.0.2` / `v10.0.3` для ключевых файлов, плюс библиотеки `mnbuhl/efcore-locking`, `PandaTechAM/be-lib-efcore-postgres-extensions`, `npgsql/efcore.pg` NodaTime и `artiomchi/FlexLabs.Upsert`, pattern Thinktecture.EntityFrameworkCore. Веб-страницы/README используются как контекст, но конкретные утверждения о hook/SQL происходят из исходников, приведённых ниже.

## 2. Реальный EF Core pipeline — куда разрешено встраиваться

```text
Consumer IQueryable<T>:
  Expression tree (C# method calls, parameters)
       ↓
  optional IQueryExpressionInterceptor (before compilation; singleton)
       ↓
  QueryableMethodTranslatingExpressionVisitor (whole-query operators)
       ↓
  Relational/Npgsql SQL tree (SelectExpression/TableExpression/...)
       ↓
  QueryTranslationPostprocessor: type mappings, projections, pruning, aliases;
  Npgsql: additional Unnest / SetOperationTyping passes
       ↓
  QuerySqlGenerator / NpgsqlQuerySqlGenerator (SQL text + parameters)
       ↓
  DbCommand / DbCommandInterceptor (execution; already-generated SQL)
       ↓
  PostgreSQL
```

* `IMethodCallTranslatorPlugin` and `IMemberTranslatorPlugin` are **additive** plugin points for scalar expressions (SQL functions/operators/type mappings), not the generic mechanism for source-level `IQueryable<T>` methods such as `.ForUpdate()`.
* `IQueryableMethodTranslatingExpressionVisitorFactory` / `IQuerySqlGeneratorFactory` each represent a **single registered service**; replacing them is invasive and can conflict with other libraries replacing the same service. Npgsql itself owns their implementations. No native additive “whole-query extension operator plugin” equivalent to `IMethodCallTranslatorPlugin` was found in the inspected version.
* `IQueryTranslationPostprocessorFactory` is another single provider-specific service; replacing it means preserving Npgsql's `NpgsqlUnnestPostprocessor` and `NpgsqlSetOperationTypingInjector`. Avoid unless pilot specifically requires a postprocessor.
* `DbCommandInterceptor` is excellent for validation, observation, error mapping, but operates after the query has lost most semantic/provenance structure. Raw SQL string editing there should not be the canonical implementation of structural locking or conflict DML.
* `IQueryExpressionInterceptor` can rewrite C# query expression **before compilation**, but needs a separate reliable carrier of typed intent through later SQL translation. It does not directly give a structural SQL clause hook.

References: EF `RelationalQueryTranslationPostprocessor.cs`, `EntityFrameworkRelationalServicesBuilder.cs`, `QueryableMethodTranslatingExpressionVisitor.cs`, `QuerySqlGenerator.cs`, Npgsql `NpgsqlServiceCollectionExtensions.cs`, `NpgsqlQueryTranslationPostprocessor.cs`, `NpgsqlQuerySqlGenerator.cs`, `NpgsqlQueryableMethodTranslatingExpressionVisitorFactory.cs` at tag v10.0.0. Exact source paths/URLs in §10.

## 3. Existing projects: mechanism-level comparison

| Implementation | Entry | Intent carrier | SQL injection point | Useful precedent | Risks and non-transferable decisions |
|---|---|---|---|---|---|
| **Pandatech EFCore.PostgresExtensions** | `IQueryable<T>.ForUpdate` | EF `TagWith("for update ...")` | `DbCommandInterceptor.ReaderExecuting(Async)` appends lock clause from first comment line | Minimal ceremony and service configuration | No full SQL AST context; outer query vs subquery and aliases/LEFT JOIN not structurally resolved; position/tag protocol fragile; no proof of safe generic shapes |
| **mnbuhl EntityFrameworkCore.Locking.PostgreSQL** | `.ForUpdate` etc | `TagWith` + `AsyncLocal<LockOptions>` | Replaces `IQuerySqlGeneratorFactory` with `NpgsqlQuerySqlGenerator` subclass; `VisitSelect` appends `FOR...` | Structural view of `SelectExpression`, targeted `OF alias` for LEFT JOIN, shape validation, correct generator family | Internal Npgsql APIs; tag/text as intent; `AsyncLocal` correctness coupling; last-tag preference with `ISet<string>` ordering; separate command interceptor can prepend `SET LOCAL` SQL; tests of more complex composition essential |
| **Official Npgsql NodaTime** | `.UseNodaTime` | `IDbContextOptionsExtension` + additive plugin registrations | `IMethodCallTranslatorPlugin`, `IMemberTranslatorPlugin`, aggregates, type-mapping plugin | Stable pattern for scalar functions and type mapping, no whole query-gen replacement | Doesn't solve whole-query `FOR UPDATE`, nor `INSERT ... ON CONFLICT` |
| **Thinktecture EF Core SQL Server** | Query-level extension e.g. table hints | Custom QMT visitor; table expression annotations | Custom query SQL generator `VisitTable` | Typed annotations on *table* expressions; overrides `CreateSubqueryVisitor` to preserve nested query behavior | SQL Server table hints are attached to FROM items; PG `FOR UPDATE` is a SELECT-level clause; cannot copy grammar mechanically; relies on internals |
| **FlexLabs.Upsert** | Typed upsert API | EF metadata and typed command description | Separate PostgreSQL DML command builder (`StringBuilder`/parameters) | `INSERT ... ON CONFLICT` can remain external to SELECT pipeline | Its broader provider-generalized DML DSL would be excess for our initial insert-ignore |
| **EFCore.BulkExtensions** | Bulk operations | Metadata + own bulk operation plan | Provider-specific COPY/commands | Shows COPY/bulk differ from query translation | Much broader scope, different licensing/provenance; don't copy complexity |

**Specific primary evidence:**

- PandaTech `QueryableExtensions.cs` calls `TagWith`, `TaggedQueryCommandInterceptor.cs` checks `command.CommandText.StartsWith("-- for update ")` and appends text at execution. Thus line ordering and shape are runtime string assumptions, not AST guarantees.
- mnbuhl `PostgresLockingQuerySqlGenerator.cs` calls `base.VisitSelect(select)` then inspects `select.Tags`, `UnsafeShapeDetector`, chooses `rootAlias` only for LEFT JOIN and appends locking SQL; `PostgresQueryableLockingExtensions` sets ambient `LockContext.Current` and inserts tag; `LockingValidationInterceptor` validates transaction and prepends `SET LOCAL lock_timeout` when applicable. Useful concrete proof that generator replacement is feasible, not proof all shapes correct.
- Thinktecture `ThinktectureSqlServerQueryableMethodTranslatingExpressionVisitor.cs` overrides `VisitMethodCall`, and `CreateSubqueryVisitor`; `ThinktectureSqlServerQuerySqlGenerator.cs` reads `TableExpression.FindAnnotation(TABLE_HINTS)` in `VisitTable`.
- Npgsql NodaTime `NpgsqlNodaTimeServiceCollectionExtensions` registers additive plugins with `TryAdd`; Npgsql provider's own services remain registered.
- FlexLabs `PostgreSqlUpsertCommandRunner` emits typed metadata-based `INSERT INTO ... ON CONFLICT ... DO NOTHING/UPDATE` as a separate command, rather than `NpgsqlQuerySqlGenerator.VisitSelect`.

### 3.1. Preserve semantics, don't infer unsafe defaults from competitors

Competitors are technical evidence but do not authorize silently locking all join tables, ignoring an unknown target alias, or auto-opening transactions. We should document **our** supported query shape matrix independently and use negative proof. `FOR ... OF` is PostgreSQL SELECT-level syntax; a table-hint approach cannot be transferred without SQL grammar review.

## 4. Crucial EF Core 10 technical limits found in source

1. **`SelectExpression` is `sealed`** (the source explicitly notes no supported public/protected subclass constructor). It is not a directly subclassable typed `PgLockedSelectExpression`.
2. **`SelectExpression.WithAnnotations` throws `UnreachableException`** at line ~4572. `SelectExpression.AddAnnotation(PgLockIntent, ...)` is therefore **not a valid implementation plan** in inspected EF 10 source, despite general `TableExpressionBase.AddAnnotation` availability. This corrects an attractive but broken abstract design.
3. `SelectExpression.Tags` is `ISet<string>` with `HashSet<string>` default. A scheme relying on `TagWith` ordering/last match for precedence needs independent proof, not assumption of an ordered list. Tags are SQL **comments** by design.
4. `TableExpression` **does implement `WithAnnotations`**, carries them via `Clone` and `Quote`, and is therefore a credible candidate for a **table-owned typed lock-intent carrier**. But its equality implementation compares inherited alias and table identity, not annotations; identity/caches must be tested, not guessed.
5. `SelectExpression.SetTables` exists and can replace table sources but is `[EntityFrameworkInternal]` and requires mutable SELECT. The standard relational pipeline later projects, prunes and remaps aliases; attaching annotations at the wrong stage can lose or misplace them.
6. `NpgsqlQuerySqlGenerator` has a visible `protected override VisitExtension(...)` handling `Pg...Expression` variants and `GenerateRootCommand(...)` to handle custom delete. This demonstrates extensible **provider-specific typed SQL expressions** but **not** that arbitrary wrapper nodes can be transparently used as EF Core relational root queries (shaper/visitor assumptions). Need real PoC.
7. `NpgsqlQueryableMethodTranslatingExpressionVisitor` has `CreateSubqueryVisitor`; a subclass that only overrides `VisitMethodCall` without preserving custom creation for subqueries will fail or silently lose custom handling in nested queries.
8. Npgsql's factory constructor exposes `INpgsqlSingletonOptions.PostgresVersion` and other settings, but this API is provider-internal; isolate in a small version-bound adapter, do not expose in public API. Its value is **SQL target** (configured/default), not actual server version.
9. Npgsql `v10.0.0 → v10.0.3` has changed SHA for generator and query-translating-visitor source; a stable constructor signature does not guarantee binary/test equivalence for SQL internals. Release test at least minimal & latest actually supported 10.x revisions.

### 4.1. Versioned compatibility boundary

Keep one source project/assembly. Group integration components that depend on internal EF/Npgsql services. Do **not** invent a generic provider interface or internal facade tree for another DBMS; if future Npgsql 11 requires different internals, make a separately verified version-compatible implementation then. No reflection-based dynamic shim to pretend universal compatibility. The current experimental `UsePostgreSqlExtensions(PocCarrier)` is idempotent for the selected carrier; service replacement conflicts and external EF internal service-provider behavior still require verification.

## 5. Row-level locking: candidates and recommendation

**Experimental source API:** `IQueryable<T>.ForUpdate()`, `ForShare()`, and `WithRowLock(LockStrength, LockWait)` express current lock requests. Stable method names, additional overloads, wait modes and any `OF` target selection remain open. Every admitted form requires an explicit caller transaction, typed intent and fail-closed handling; ordinary queries without a lock request must retain native Npgsql behavior. No multi-table target API is selected.

### 5.1. Carrier A: translator + typed table marker + SQL generator

```text
IQueryable<T>.ForUpdate(...) // creates Expression.Call for an EF-recognized query operator
  → derived NpgsqlQueryableMethodTranslatingExpressionVisitor.VisitMethodCall
     → translate source; validate lock/options and structural phase
     → assign typed lock intent to eligible TableExpression(s), if safely representable
  → existing EF/Npgsql postprocessors (do NOT replace by default)
  → derived NpgsqlQuerySqlGenerator.VisitSelect
     → find compatible typed table markers within exact SELECT scope
     → validate legal SQL shape, table alias and target resolution
     → emit `FOR UPDATE [OF alias, ...] [NOWAIT | SKIP LOCKED]`
  → optional command interceptor for active transaction proof (no SQL rewrite)
```

The current PoC implements this carrier: source-level typed intent, no tag comments or ambient state, and SQL generation owns quoting and alias placement. Query-cache and alias behavior still require runtime evidence. The implementation uses targeted provider-service replacements.

The bounded local PostgreSQL checks cover selected root locks, ordering/limit, and synchronous/asynchronous compiled-query forms across A/B/C. Explicitly exercised relational shapes that are outside the supported probe scope reject before issuing a SELECT. Arbitrary unmarked-predicate subqueries and global query filters remain uncharacterized. No carrier is selected for production.

### 5.2. Carrier B: query-translator tag

The current PoC processes the public typed marker in an Npgsql-derived query visitor, converts it to a reserved internal tag, and consumes it in the structural SQL generator. B participates in the bounded checks above; those checks do not characterize arbitrary unmarked-predicate subqueries or global query filters. B is not selected for production.

### 5.3. Carrier C: expression-interceptor tag

The current PoC processes the marker in an `IQueryExpressionInterceptor` and consumes a reserved internal tag in the structural generator. C participates in the bounded checks above; those checks do not characterize arbitrary unmarked-predicate subqueries or global query filters. C is not selected for production.

The source keeps A/B/C as comparison probes until maintainers choose a supported carrier and shape set. Remove losing probes from the production implementation after that decision.

### 5.4. Rejected command-text rewriting

`DbCommandInterceptor` may validate execution preconditions, including an active transaction and carrier-fulfillment receipt, or observe diagnostics. It must never add row-lock semantics by patching generated SQL. There is no textual fallback if a carrier fails.

### 5.5. Exact SQL and semantic questions to prove

| Query family | Acceptance question |
|---|---|
| `Where → ForUpdate → Single`, `ForUpdate → Where` | Is method recognized and lock attached to the intended SQL-level query in either order? |
| Single table with projection and tracking/no-tracking | Do selected physical rows get locked? Any required tracking semantics? |
| Inner JOIN / self JOIN, multiple tables | Which aliases get `FOR ... OF`, can caller select exact entity/table targets? |
| LEFT/RIGHT/FULL joins | Avoid attempting to lock nullable join side; fail loud if no safe target selection |
| Limit/Offset/OrderBy | Placement correct; PostgreSQL READ COMMITTED behavior (order/row set) preserved and documented |
| Subquery/pushdown, nested composition | Lock marker remains in intended SELECT rather than leaking to outer query or disappearing |
| Distinct/GroupBy/aggregate/Union/Intersect/Except | Reject unsupported restrictions before unlocked runtime behavior |
| `Include`, `AsSplitQuery`, `Any`, `Count` | If semantics not provable, reject; no silent lock on different statement |
| `EF.CompileQuery`/`EF.CompileAsyncQuery`, DbContextPool, AddDbContextFactory | SQL and state isolation correct; no static/AsyncLocal state |
| Different Npgsql singleton options / SQL target | Compiled SQL only reused when configuration-compatible; fail loud on invalid version target |
| Provider plugins co-installed | NodaTime and ordinary translators unaffected; competing singleton factory replacement handled explicitly |
| Real PG 2 sessions / borrowed tx | Actual locking, timeout, NOWAIT and rollback/commit release behavior matches generated SQL |

**Do not overpromise** arbitrary composability just because the fluent C# method compiles. The public API should only support query forms the library has a verified structural and runtime semantics for; growing matrix is valid way to scale.

## 6. Other capabilities: appropriate mechanisms

### 6.1. Database clock — additive scalar plugin

`DatabaseClock.ClockTimestamp()` is currently mapped through the model `DbFunction` extension `MapClockTimestamp()`. PostgreSQL `clock_timestamp()` observes wall clock, whereas `now()/transaction_timestamp()` is transaction-start timestamp. Npgsql's built-in UTC translation can resolve to `now()`; do not silently change semantics. Verify type mapping (e.g. timestamptz → CLR type), UTC semantics, composition, parameter/query cache and server-side-only failure on client invocation. Avoid requiring a separate second call/extra SELECT on normal queries.

### 6.2. Advisory locks — DatabaseFacade command helper

`DatabaseFacade.AcquireTransactionAdvisoryLockAsync(...)` should use the EF-managed `DbConnection` and **borrow existing `CurrentTransaction`**, issue parameterized PostgreSQL command (library-owned technical layer), forward cancellation and not create/commit/savepoint/retry transactions. The text-key legacy parity mode must compute `pg_advisory_xact_lock(hashtextextended(@text, 0))` server-side, not `.GetHashCode()`. No SQL generator or queryable visitor replacement. Verify two separate sessions and pooled connections do not leak session locks.

### 6.3. Insert-if-absent — narrow metadata-aware DML

Typed `DbSet<T>` or `DbContext` entry → EF model store object/property metadata, value converters and parameters → PostgreSQL `INSERT ... ON CONFLICT (...) DO NOTHING` → EF connection/transaction → affected rows or clear success result. This is independent of SELECT query generation; do not hijack `IUpdateSqlGenerator` globally for SaveChanges unless an evidence-backed compatibility reason emerges. Keep first API narrow (single-row insert-ignore) and explicitly rule out unsupported generated columns, computed properties, filtered/partial unique indexes, inheritance strategies or `ON CONSTRAINT` resolution unless implemented and tested. Do not pretend all `IQueryable` can insert. Independent EF tracking needs explicit semantics and tests; no ghost tracked entity after ignored insert. Existing EF `ExecuteUpdate` is preferred for CAS/update patterns, not a generic replacement DSL.

### 6.4. Capability discovery and compatibility validation

Read effective target from Npgsql configuration (provider internal adapter isolated), actual version from opened `NpgsqlConnection.PostgreSqlVersion`, extensions from `pg_extension` at validation time. No query visitor probes DB at translation time and no runtime query shape selection per command. Distinguish declared requirements and installed extension facts, a global requirement to install all extensions. Startup gate is an optional/explicit host orchestration concern; the library never runs `CREATE EXTENSION` or migrations itself.

## 7. Registration / lifecycle and plugin collision policy

Candidate public registration:

```csharp
options.UseNpgsql(connectionString, npgsql =>
    npgsql.UsePostgreSqlExtensions(PocCarrier.ExpressionTag));
```

The current experimental overload requires an explicit `PocCarrier` argument; no parameterless production registration is admitted. Selecting this argument chooses a comparison probe, not an approved production carrier.

The experimental query registration uses a scoped options extension and carrier-specific service registration. Before admission, `OptionsExtensionInfo` hashing/equality must reflect every option that changes generated SQL. Never use per-request mutable singleton state. Avoid requiring a host DI container for simple technical helpers.

**Multiple SQL generator replacements:** EF Core has no built-in additive `IQuerySqlGeneratorFactory` chain. Another extension may replace that same service. It is not enough to register ours last: that can silently disable the other extension. At minimum detect known incompatible configurations and throw before accepting queries; allow explicit tested composition only with concrete evidence. Do not create a hypothetical decorator registry for arbitrary third-party generators at baseline.

## 8. Technical decision matrix

| Feature | Recommended mechanism | Confidence and status |
|---|---|---|
| Scalar SQL functions (`clock_timestamp`) | Additive `IMethodCallTranslatorPlugin` or precise `DbFunction` mapping | High on mechanism, exact signatures pending small PoC |
| Transaction advisory locks | Typed `DatabaseFacade` command using existing EF transaction | High on mechanism, key/connection details pending PG proof |
| Insert-if-absent | Narrow, EF metadata-aware, parameterized DML builder | Medium-high, mapping/tracking/uniqueness proof pending |
| `IQueryable` row locking | Three experimental structural carriers: typed table annotation, QMT tag, and query-expression-interceptor tag; shared Npgsql SQL generator | Bounded local PostgreSQL checks cover selected roots, ordering/limit, and compiled-query forms; production carrier and shape scope remain unselected |
| `SelectExpression.AddAnnotation` | Unsupported in EF Core v10.0.0 (`WithAnnotations` throws) | Source-proven rejection |
| `TagWith`/`AsyncLocal` for core intent | B/C use a reserved internal tag only after typed-marker processing; no `AsyncLocal` | Experimental carriers; preserve spoof, scope, cache and fail-closed checks |
| `DbCommandInterceptor` text rewrite | Not structural SQL locking backbone | Not recommended |
| New provider-neutral ORM SQL AST/framework | No demonstrated need | Reject as unjustified cost |
| Npgsql configured PostgreSQL target | Sole source for SQL generation target | Owner accepted |
| Actual PostgreSQL version/extensions | Only runtime capability validation, not query compilation selector | Owner accepted direction |

**Overall:** use a small **hybrid integration layer**; follow EF Core additive APIs whenever sufficient and use deeper internal provider hooks only for whole-query semantics. `Npgsql10` internals must be isolated from public API. Typed or tagged lock intent may be introduced only if it survives the complete EF/Npgsql pipeline and real PostgreSQL concurrency tests with exact targets.

## 9. Decision gate before freezing row locking implementation

Compare A/B/C using nested queries, joins/alias scope and compiled queries, together with real PostgreSQL locking behavior. SQL text containing `FOR UPDATE` alone is insufficient. Any admitted carrier must preserve ordinary EF operations when registered and reject unsupported shapes before unlocked execution. If no candidate proves this, narrow functionality or defer row locking; do not claim generalized support.

See [detailed PoC gates](query-gates.md).

## 10. Primary-source index

The following source paths were verified directly in GitHub at indicated tags on 2026-10-16:

- EF Core `v10.0.0`: [`SelectExpression`](https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore.Relational/Query/SqlExpressions/SelectExpression.cs), [`TableExpression`](https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore.Relational/Query/SqlExpressions/TableExpression.cs), [`TableExpressionBase`](https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore.Relational/Query/SqlExpressions/TableExpressionBase.cs), [`QuerySqlGenerator`](https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore.Relational/Query/QuerySqlGenerator.cs), [`QueryableMethodTranslatingExpressionVisitor`](https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore/Query/QueryableMethodTranslatingExpressionVisitor.cs), [`RelationalQueryTranslationPostprocessor`](https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore.Relational/Query/RelationalQueryTranslationPostprocessor.cs), [`IQueryExpressionInterceptor`](https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore/Diagnostics/IQueryExpressionInterceptor.cs), [`EntityFrameworkRelationalServicesBuilder`](https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore.Relational/Infrastructure/EntityFrameworkRelationalServicesBuilder.cs).
- Npgsql EF Core `v10.0.0`: [`NpgsqlQuerySqlGenerator`](https://github.com/npgsql/efcore.pg/blob/v10.0.0/src/EFCore.PG/Query/Internal/NpgsqlQuerySqlGenerator.cs), [`NpgsqlQuerySqlGeneratorFactory`](https://github.com/npgsql/efcore.pg/blob/v10.0.0/src/EFCore.PG/Query/Internal/NpgsqlQuerySqlGeneratorFactory.cs), [`NpgsqlQueryableMethodTranslatingExpressionVisitor`](https://github.com/npgsql/efcore.pg/blob/v10.0.0/src/EFCore.PG/Query/Internal/NpgsqlQueryableMethodTranslatingExpressionVisitor.cs), [`NpgsqlQueryTranslationPostprocessor`](https://github.com/npgsql/efcore.pg/blob/v10.0.0/src/EFCore.PG/Query/Internal/NpgsqlQueryTranslationPostprocessor.cs), [`NpgsqlServiceCollectionExtensions`](https://github.com/npgsql/efcore.pg/blob/v10.0.0/src/EFCore.PG/Extensions/NpgsqlServiceCollectionExtensions.cs), [`Npgsql NodaTime plugin registrations`](https://github.com/npgsql/efcore.pg/blob/v10.0.0/src/EFCore.PG.NodaTime/Extensions/NpgsqlNodaTimeServiceCollectionExtensions.cs).
- mnbuhl: [`PostgresLockingQuerySqlGenerator`](https://github.com/mnbuhl/efcore-locking/blob/main/src/EntityFrameworkCore.Locking.PostgreSQL/PostgresLockingQuerySqlGenerator.cs), [`PostgresLockingServiceCollectionExtensions`](https://github.com/mnbuhl/efcore-locking/blob/main/src/EntityFrameworkCore.Locking.PostgreSQL/PostgresLockingServiceCollectionExtensions.cs), [`LockingValidationInterceptor`](https://github.com/mnbuhl/efcore-locking/blob/main/src/EntityFrameworkCore.Locking/Internal/LockingValidationInterceptor.cs), [`UnsafeShapeDetector`](https://github.com/mnbuhl/efcore-locking/blob/main/src/EntityFrameworkCore.Locking/Internal/UnsafeShapeDetector.cs).
- PandaTech: [`TaggedQueryCommandInterceptor`](https://github.com/PandaTechAM/be-lib-efcore-postgres-extensions/blob/main/src/EFCore.PostgresExtensions/Interceptors/TaggedQueryCommandInterceptor.cs), [`QueryableExtensions`](https://github.com/PandaTechAM/be-lib-efcore-postgres-extensions/blob/main/src/EFCore.PostgresExtensions/Extensions/QueryableExtensions.cs).
- Thinktecture: [`ThinktectureSqlServerQueryableMethodTranslatingExpressionVisitor`](https://github.com/PawelGerr/Thinktecture.EntityFrameworkCore/blob/main/src/Thinktecture.EntityFrameworkCore.SqlServer/EntityFrameworkCore/Query/ThinktectureSqlServerQueryableMethodTranslatingExpressionVisitor.cs), [`ThinktectureSqlServerQuerySqlGenerator`](https://github.com/PawelGerr/Thinktecture.EntityFrameworkCore/blob/main/src/Thinktecture.EntityFrameworkCore.SqlServer/EntityFrameworkCore/Query/ThinktectureSqlServerQuerySqlGenerator.cs).
- FlexLabs: `src/FlexLabs.EntityFrameworkCore.Upsert/Runners/PostgreSqlUpsertCommandRunner.cs` in https://github.com/artiomchi/FlexLabs.Upsert; implements separate `INSERT ... ON CONFLICT` builder.
- PostgreSQL [`SELECT` locking clauses](https://www.postgresql.org/docs/current/sql-select.html), EF Core [`Interceptors`](https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors), Npgsql [`Function translations`](https://www.npgsql.org/efcore/mapping/translations.html).

No third-party source code was copied into prospective library. Any adaptation of original implementation requires separate licence review and attribution.
