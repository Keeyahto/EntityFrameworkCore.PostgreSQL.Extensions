# EntityFrameworkCore.PostgreSQL.Extensions — архитектура публичной библиотеки

This repository develops one PostgreSQL-specific EF Core/Npgsql library. The four integration boundaries and their constraints are the design baseline. The [first-production contract](briefs/production-contract.md) defines the selected expression-interceptor implementation, public API, finite query grammar and reference evidence target. The implementation is present in the runtime project; provider behavior, package-consumer compatibility and release claims depend on their own current evidence. NuGet publication remains a separate release decision, and detailed verification results belong with change evidence.

## A. Product contract и позиционирование

Библиотека предоставляет **общие PostgreSQL-specific типизированные extensions к EF Core/Npgsql**, когда подходящего штатного EF/Npgsql API нет. Не знает application, owner modules, их SQL names, бизнес-ошибки или транзакционную политику. Спроектирована для повторного использования другими продуктами и будущей публикации на NuGet; публикация — отдельное разрешаемое событие. Предназначена для .NET-разработчика, знакомого с EF Core: один NuGet-пакет, одна import namespace, optional fluent registration только для функций, требующих конфигурации provider.

Отличия от уже существующих `mnbuhl/efcore-locking`, `Pandatech.EFCore.PostgresExtensions`: больше внимания структурной корректности SQL query shapes/aliases, fail-loud при unsupported shape, точной transaction-scoped PostgreSQL-семантике и атомарной вставке. Новизна каждого публичного API должна подтверждаться сравнением с текущим Npgsql и аналогами. **Никакого обещания поддержки unsupported query shape через post-SQL string rewrite.**

| Невходящие обязанности | Основание |
|---|---|
| Произвольный Repository/UoW, собственный ORM или generic SQL dispatcher | Дублирование EF; ломает ожидаемый опыт потребителя |
| Application tenant/actor/basis/receipt/unknown-commit policy | Нарушение независимости и domain ownership |
| Автоматический retry, transaction begin/commit, distributed lock orchestration | PostgreSQL primitive не выбирает владельческие гарантии |
| Универсальный bulk COPY/UPSERT/DDL framework v1 | Нет подтверждённого API и workload |
| Поддержка всех .NET/EF/Npgsql/PostgreSQL версий с первого релиза | Слишком большая матрица и недостоверная гарантия |
| Обязательные decorators, global mutable ambient state, сервисы, требующие DI всегда | Лишняя ceremony и риск composition/DbContextPooling |

## B. Package и размещение

The public repository is independently buildable and contains one runtime project and one test project. It uses Apache-2.0; its upstream is `https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions`. The project declares package ID `EntityFrameworkCore.PostgreSQL.Extensions` and version `0.1.0-preview.3`; NuGet ownership/availability and publication remain release decisions. Keep one packable project and one public assembly; do not add microprojects for conceptual layers. Use internal feature components and expose only the EF-facing API. A separate query-plugin package requires demonstrated provider-internal or plugin compatibility need.

CI, build/test commands, README, contribution process, changelog and compatibility limits are maintained in this checkout. Library source/package evidence and each downstream consumer update have separate change records; two repositories do not form an atomic transaction.

### Current checkout

```text
EntityFrameworkCore.PostgreSQL.Extensions/
  src/EntityFrameworkCore.PostgreSQL.Extensions/
    Query/
    Commands/
    Dml/
    Functions/
    Compatibility/
  tests/EntityFrameworkCore.PostgreSQL.Extensions.Tests/
  docs/
  .github/workflows/poc.yml
  README.md
  LICENSE
  CHANGELOG.md
  CONTRIBUTING.md
  SECURITY.md
  Directory.Build.props
  Directory.Packages.props
  global.json
  EntityFrameworkCore.PostgreSQL.Extensions.slnx
```

Не создавать отдельный docs website/framework, benchmarks project, multi-package releases и logo generator до реальной потребности. Benchmarks можно добавить после измеренного performance-sensitive plugin path.

## C. .NET/toolchain/dependencies

The current target is `net10.0` with `global.json` requesting SDK `10.0.100`, `rollForward=latestFeature`, and prereleases disabled. The reference resolution is SDK 10.0.401; this request is not an exact SDK pin for consumers. TFM and SDK remain separate compatibility axes. CI requests a stable .NET 10 SDK; build, SourceLink and package claims require their own evidence.

The current central package pins are Npgsql EF provider `10.0.3`, Npgsql `10.0.3`, and EF Core Relational `10.0.12`; the real-provider fixture uses the pinned PostgreSQL image at version `16.14`. These are the reference evidence inputs, not a verified consumer compatibility matrix. EF Core 8/9/11, other PostgreSQL majors and additional package revisions require separate evidence before being claimed. Library package version remains independent of EF version.

- Dev/build зависимости выбираются через один `Directory.Packages.props`; test libraries `PrivateAssets` там, где должны оставаться dev-only.
- При заявлении публичной поддержки минимальные EF/Npgsql package revisions и совместимая major должны соответствовать проверенным версиям. Текущие package metadata задают минимумы и верхнюю границу `< 11.0.0`; сами интервалы не подтверждают совместимость каждой версии внутри них. Upper bounds должны соответствовать явной version policy или доказанной несовместимости; exact patch не следует навязывать downstream consumers.
- Не требуется direct package reference на все transitive EF/Microsoft библиотеки, если Npgsql уже предоставляет необходимые references; пакет должен иметь минимальные реальные зависимости.
- Library не навязывает transitive analyzers, formatting package, DI dependencies или build target consumer-у.
- `Nullable=enable`, latest stable C# для текущего TFM без preview, deterministic builds, embedded symbols/SourceLink, generated XML doc для public surface, warnings as errors **для своего CI/своих sources**. Не навязывать чужим проектам уровень analyzers и optional code style.
- Multi-targeting и NativeAOT/trimming capability **не объявлять supported** до соответствующего runtime evidence. EF compiled queries/pooling/DI plugins проверять независимо.

Пример `global.json`:
```json
{"sdk":{"version":"10.0.100","rollForward":"latestFeature","allowPrerelease":false}}
```

## D. Публичная эргономика и namespace

**Утверждённая эргономическая граница:** один user-facing `using EntityFrameworkCore.PostgreSQL.Extensions;`; публичные extension methods группируются по типу получателя: `IQueryable<T>` для read-query behavior, `DatabaseFacade` для transaction-scoped SQL commands, `DbSet<T>/DbContext` для typed DML, `EF.Functions` для server-side PostgreSQL functions. Не инжектировать отдельный framework/application service, если есть естественный EF API. Не расширять namespace `Microsoft.EntityFrameworkCore` просто ради удаления одного using: это снижает риск имен/overload-коллизий с другими packages.

Row-lock queries use `options.UseNpgsql(cs, npgsql => npgsql.UsePostgreSqlExtensions());`. The parameterless registration is idempotent and enables the expression-interceptor path described below. It keeps Npgsql's native query translator and replaces the query SQL-generator factory; the query-expression interceptor validates and carries immutable request intent, while its command-interceptor role validates fulfillment and the current transaction without modifying SQL. Other mechanisms do not need this query-plugin registration. Registration composition and compatibility claims require evidence for the exact supported forms.

### D1. Row-level locking

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);

var resource = await db.Resources
    .Where(x => x.Id == id)
    .ForUpdate(LockWait.NoWait)
    .SingleAsync(ct);

resource.State = nextState;
await db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

The public API exposes `ForUpdate`, `ForShare`, and `WithRowLock(LockStrength, LockWait)` for one unambiguous root entity occurrence. Parameterless overloads use `Wait`; enum arguments are compile-time constants. A physical lock requires the current explicit caller-owned transaction on the command connection and remains until that transaction completes. The generator selects the exact generated alias structurally; callers cannot provide raw table names. Unsupported shapes must fail before the requested SELECT is issued. `ORDER BY` does not prove a deterministic lock acquisition order or post-wait ranking across concurrent updates.

The initial closed grammar is one directly mapped, unfiltered root table; entity materialization; conjunctions of direct mapped scalar equality predicates against constants or extracted EF parameters; supported entity terminals or enumeration; and an optional direct scalar ordering chain followed by `Take(1)`. Tracking and no-tracking roots are eligible. Exact signatures and mapping/composition limits are in the [first-production contract](briefs/production-contract.md). Joins, projections, includes/split queries, global filters, navigation/subquery predicates, raw SQL roots, inheritance/table fragments, offsets, arbitrary limits, aggregates, set operations, repeated lock requests and unknown shapes are rejected. An operator name alone does not admit an arbitrary composition. Untested mappings remain unadmitted.

The query-expression interceptor validates the original expression and finalized model, then carries the typed request through a reserved internal tag. The native Npgsql translator remains active. The structural generator validates the final SELECT and root table occurrence before emitting `FOR <strength> OF <quoted alias>` and the requested wait mode. The command interceptor checks the matching fulfillment receipt, active service identities, exact execution transaction/connection and bounded selection parameter on sync and async execution. It does not rewrite SQL text. Ordinary unmarked queries retain native behavior. The previous annotation and translator-tag carriers are research comparisons and have no production runtime path.

### D2. Advisory locks

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);
await db.Database.AcquireTransactionAdvisoryLockAsync(reference, ct);
// caller-owned protected work
await tx.CommitAsync(ct);
```

One active PostgreSQL transaction, exact `pg_advisory_xact_lock(hashtextextended(@text, 0))` behavior for text keys. This is a PostgreSQL-side hash, **not** .NET `GetHashCode()`. Other primary typed keys are 64-bit and 2×int32; they live in separate PostgreSQL key spaces. Text hashing is not collision-free and shares the bigint key space. No session-scoped fallback or try-lock API. The caller selects a stable key protocol and its relation to domain. The helper executes under the current DbContext connection and transaction, releases nothing manually, and does not open or commit a transaction. Cancellation propagates; the library adds no retry or collision handling.

### D3. Atomic `ON CONFLICT DO NOTHING`

```csharp
var inserted = await db.InsertIgnoreAsync(
    new ReservationRow { ... },
    conflictTarget: x => new { x.ResourceId, x.ReservationKey }, ct);
// inserted == true exactly when INSERT affected a row
```

`InsertIgnoreAsync` accepts one entity and a direct mapped property or anonymous composite selector backed by a primary/alternate key or unfiltered unique index. It uses EF metadata, provider quoting, type mappings/converters and parameters. It returns whether PostgreSQL inserted one row; only the selected conflict is ignored, while unrelated constraints propagate. It borrows an existing caller transaction or executes one statement using PostgreSQL autocommit, without creating a transaction. Its one-table mapping and generated/default/sentinel rules are deliberately bounded. It does not attach or mutate the entity, run key generators or `SaveChanges` interceptors, return generated values, implement `DO UPDATE`, or perform check-then-insert. See the [first-production contract](briefs/production-contract.md) for unsupported mappings and explicit-NULL limits.

### D4. Database time/functions and technical support

```csharp
var timestamp = await db.Resources
    .Select(_ => DatabaseClock.ClockTimestamp())
    .FirstAsync(ct);
```

Call `modelBuilder.MapClockTimestamp()` while building the model to map this database-only function.

`clock_timestamp()` is wall time at evaluation; PostgreSQL `now()` is transaction start. `MapClockTimestamp()` maps `DatabaseClock.ClockTimestamp()` as a model `DbFunction`; direct client invocation throws. There is no .NET client-time fallback. Repeated function calls may return different values within one SQL statement. `Database.ReadClockTimestampAsync()` performs one native server clock read, returns a UTC `DateTime` at PostgreSQL precision, borrows an attached current transaction when present, and balances only connection ownership acquired by the call. It does not impose TTL, interval, overflow, or compound-statement policy. Other mapping and single-evaluation forms need their own evidence before they are claimed.

`DatabaseFacade.SetTransactionReadOnlyAsync` issues native `SET TRANSACTION READ ONLY` under an existing caller transaction. PostgreSQL controls command timing and write restrictions; the helper creates no transaction, changes no session default and selects no isolation level. `PostgreSqlCapabilityProbe.CheckAsync` is a caller-invoked read-only report of the effective SQL target, one observed server version, installed extensions and the caller's declared requirements. Its outcomes are `Compatible`, `Incompatible` and `Indeterminate` for those requirements and observations. It does not install extensions, change the SQL target, certify other databases or roles, or manage host readiness. SQLSTATE domain classification, `ON CONFLICT DO UPDATE` and array/JSONB convenience operators are outside the current API.

## E. Cross-cutting correctness contracts

| Condition | Behavior |
|---|---|
| Wrong database provider | Deterministic explicit unsupported-provider failure; never best-effort execute or silently return no-op |
| Unsupported LINQ query shape | Explicit translation/rejection; never unlock a query by ignoring a lock marker |
| Missing explicit transaction for advisory/row locks | Explicit failure before those lock operations execute; they never silently create one |
| Caller cancellation | Forward `CancellationToken`; do not swallow or retry; PostgreSQL's uncertain commit remains caller/owner responsibility |
| Row-lock plugin registration and competing replacements | Repeated registration is idempotent; conflicting query-generator/translator replacements and manually configured internal service providers fail explicitly |
| DbContext pooling, scoped vs singleton services | No ambient `AsyncLocal` for correctness, no process-global mutable lock state; no per-request state in provider singleton |
| Bad EF entity mapping/conflict target | Clear fail-fast exception, no raw unquoted identifier acceptance |
| Observability | EF `ILogger`/Activity support where natural; no credentials or arbitrary SQL parameter logging by default |
| Performance | No hidden N+1 queries or silent re-materialization; real SQL shape and query-plan comparison where relevant |

Exceptions should normally use established EF/Npgsql and .NET exception classes with useful messages. New public custom exception hierarchy only when a concrete cross-feature catch/diagnostic need is identified, not for every failure. Avoid ordinary owner semantics in common exception types.

## F. Test strategy: capability-first, negative cases and concurrency

- Unit tests for public API argument validation and query shape, no unnecessary thousand tests for incidental code.
- Real PostgreSQL integration tests with isolated temporary database/container and >= 2 sessions for `FOR UPDATE`/advisory/concurrent insert. PostgreSQL 16 primary initially; cross-version matrix expanded before claiming version support. Verify commit, rollback, cancellation, transaction pooling, timeout, unexpected connection closure, provider mismatch.
- SQL structural assertions + `EXPLAIN`/plan/performance baseline where query shape matters. No full SQL snapshots as the sole correctness oracle.
- Consumer example tested by compiling/running against **packed** `.nupkg` in local isolated NuGet feed, not solely via direct `ProjectReference`. Catch missing package dependencies, docs mistakes and unsupported type surfaces.
- Compatibility tests for patched EF Core/Npgsql versions as selected; other Npgsql plugins/interceptors, `AddDbContextPool`, `AddDbContextFactory` and concurrency of independent contexts.
- `EnablePackageValidation=true` for package; public API baseline comparison after first stable version. Avoid arbitrary minimum coverage percentage; each public feature has evidence of its functional and failure semantics.
- Current GitHub Actions configuration: Linux restore, Release build, full tests, pack and isolated real-PostgreSQL packed-consumer run; Windows restore, Release build and unit tests. Workflow configuration is not a run result. A later compatibility sweep can cover additional PostgreSQL/SDK versions. Do not run an expensive all-version Cartesian product on every PR unless necessary.

## G. Developer experience / documentation / open-source readiness

- Before publication, README should state purpose, package installation instructions, the exact verified support scope, a working example, warnings/limitations and feature-doc links.
- Per-feature docs: API examples, SQL generated, transaction expectations, supported/unsupported query shapes, multi-session examples, EF query tracking and parameter security.
- Docs build from real compiler-tested snippets if inexpensive; at minimum samples compile and are run in CI. XML docs on all exported symbols; IntelliSense usage is primary documentation surface.
- `LICENSE`: Apache-2.0 is present in the public repository. Check third-party license terms and preserve required attribution before copying any code.
- `CONTRIBUTING.md`: one short developer setup and review rules; `SECURITY.md`: non-public reporting instructions; `CHANGELOG.md`: meaningful user-facing change history. No issue template zoo or website generator until contributors appear.
- NuGet metadata: `PackageId`, `PackageDescription`, version, author, SPDX license, repository/source URL, embedded README, Source Link/symbol package, deterministic build; pack-and-inspect gate.
- SemVer for public package, preview release before v1.0.0; no commitment to preserve unstable API beyond stated preview policy. Automatic NuGet release on tagged signed/approved release with GitHub OIDC **Trusted Publishing** (when NuGet owner/policy configured). Never put long-lived NuGet API keys in CI by default.
- Release notes tie plugin behavior to EF/Npgsql tested versions; dependency update PRs run integration tests. Popularity comes from robust documented capabilities and a trustable release pipeline, not badges/boilerplate count.

## H. Current architecture boundaries

| Concern | Current constraint |
|---|---|
| Library boundary | One PostgreSQL-specific EF Core/Npgsql package and runtime assembly, one public namespace, no application-domain or transaction-policy ownership |
| Query integration | Parameterless provider registration, expression-interceptor intent transport, native Npgsql translator, one structural SQL-generator replacement and command-time fulfillment/transaction validation |
| Mechanisms | Four independent boundaries: query integration, scalar expressions, command execution and typed DML; technical helpers do not create a generic operation framework |
| Transaction ownership | Advisory and row locks require the caller's explicit transaction; other helpers borrow a current transaction where supported and create none |
| Fail-closed behavior | Unsupported row-lock shapes, mappings or conflicting provider-service composition fail explicitly; locking SQL is never added by rewriting command text |
| Compatibility | `net10.0` and the exact reference package/server inputs in [compatibility](compatibility.md); no broader consumer matrix is inferred |
| Release | Preview project metadata is present; package identity ownership, publication and stable API commitments remain separate release decisions |
| Excluded scope | No additional TFM, NativeAOT/trimming claim, EF 8/9/11 compatibility claim, generic bulk/extended DSL, SQL target reconfiguration or host lifecycle |


## I. Primary references

- EF plugins: https://learn.microsoft.com/en-us/ef/core/miscellaneous/plugins
- Npgsql SQL generator internals disclaimer: https://www.npgsql.org/efcore/api/Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal.NpgsqlQuerySqlGeneratorFactory.html
- EFCore.PG 10: https://www.npgsql.org/efcore/release-notes/10.0.html
- .NET SDK global.json: https://learn.microsoft.com/en-us/dotnet/core/tools/global-json
- NuGet library deps: https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/dependencies
- NuGet package authoring: https://learn.microsoft.com/en-us/nuget/create-packages/package-authoring-best-practices
- Package validation: https://learn.microsoft.com/en-us/dotnet/fundamentals/package-validation/overview
- NuGet Trusted Publishing: https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing
- Comparator: https://github.com/mnbuhl/efcore-locking
- Comparator: https://www.nuget.org/packages/Pandatech.EFCore.PostgresExtensions


## J. Compatibility evidence

The query adapter uses provider-internal EF Core/Npgsql SQL-generator APIs. Build compatibility for one exact dependency set does not prove that a packed library works with another patch. Keep separate evidence for (1) `.NET TFM` and resolved SDK, (2) EF/Npgsql packages, (3) PostgreSQL server, (4) configured Npgsql SQL target, and (5) the library package/API. Each axis has its own oracle.

**Минимальный, без собственного framework:**

1. For package usability evidence, run `build → unit → real PostgreSQL integration → dotnet pack → isolated consumer run` with the selected inputs. `scripts/Test-Package.ps1` installs the exact local `.nupkg` into a fresh local feed/cache, uses a consumer without `ProjectReference`, checks the loaded package assembly and exercises the real PostgreSQL consumer sample. The configured Linux workflow includes this runner; its presence alone supplies no run result.
2. Any published support range needs evidence at its declared boundaries. The current project declares dependency minima and a major-10 upper bound in package metadata, while the reference build/test set is EF Core Relational 10.0.12 and Npgsql packages 10.0.3. Those intervals do not certify every 10.x revision. Do not resolve an implicit "latest" version or claim an untested boundary. Do not run every EF × SDK × PostgreSQL combination in each PR.
3. PostgreSQL server: integration and packed-consumer evidence use the pinned PostgreSQL 16.14 reference image. Add another server version only for a support claim that names it; `docs/compatibility.md` records target inputs and claim boundaries. Do not promise untested PostgreSQL patch or major versions.
4. Обновления: dependabot NuGet PR с разумной периодичностью (например еженедельно) и grouping patch updates, пропускать их через основной CI. Никакого самописного «latest version resolver», custom compatibility test runner, realtime watcher или огромной динамической matrix.
5. Public library API: use standard `EnablePackageValidation`; compare against the prior stable NuGet API after the first stable release. Do not build a custom ABI or SemVer comparator.
6. Dependency policy: project test/build inputs remain explicit for reproducibility. Package metadata currently declares minimum EF Core Relational 10.0.12 and Npgsql 10.0.3, with ranges below major 11. Those ranges are not a compatibility certificate; maintainers must base broader public support claims on their corresponding provider and packed-consumer evidence. Avoid adding unsupported patch or major guarantees.

### Что сознательно не добавляем

- Второй совместимый TFM, поддержку EF 8/9/11, тестовую матрицу всех исторических версий PostgreSQL, NativeAOT/trimming claims без конкретного потребителя.
- Собственный analyzer применения библиотеки, «CompatibilityRegistry», генератор compatibility-документов или отдельный проект для матрицы.
- Негарантируемую поддержку query shapes с fallback до unlocked SQL.
- Fixing one SDK patch with `rollForward=disable`; the current `10.0.100/latestFeature` request allows feature-band roll-forward, and the resolved SDK remains a distinct evidence input.

The isolated packed-consumer run is part of the configured Linux verification path. Package usability and declared support remain conclusions of exact current evidence, not of the workflow definition. Matrix expansion beyond a named support claim and routine dependency-update automation remain optional maintenance recommendations.

**Official verification references:**

- https://learn.microsoft.com/en-us/dotnet/fundamentals/package-validation/overview
- https://learn.microsoft.com/en-us/nuget/concepts/package-versioning
- https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files
- https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/configure-version-updates


## K. Current mechanism and admission status

The four independent technical integration mechanisms are the design baseline. They are not four projects, required interfaces, an operation framework, or a closed taxonomy.

| Mechanism | Класс задачи | Предпочтительная точка интеграции | Статус реализации |
|---|---|---|---|
| Query Integration | Row-level locking | Expression interceptor, reserved intent/fulfillment tags, native Npgsql translator and structural SQL generation | Current source uses the selected parameterless registration and closed root grammar; provider and package claims remain evidence-bound |
| Scalar Expression Integration | PostgreSQL functions and one-value clock read | Native EF `DbFunction` mapping plus a typed `DatabaseFacade` clock read | `clock_timestamp()` mapping and `ReadClockTimestampAsync` are implemented; mappings beyond their explicit contract require separate evidence |
| Command Execution | Transaction-scoped advisory locks and read-only transaction command | Parameterized `DatabaseFacade` commands under the caller's existing transaction | Text, bigint and two-int advisory locks plus `SetTransactionReadOnlyAsync` are implemented; they create no transaction |
| Typed DML Construction | `INSERT ... ON CONFLICT DO NOTHING` | EF metadata, typed values, relational mappings and parameterized PostgreSQL DML | Single-row selected-key insert-ignore is implemented; mapping, sentinel, tracking and provider limits are in the contract |

**Cross-cutting guarantees:** no silent unlocked query; no implicit transaction/commit/retry; no domain/application dependency; no raw SQL text patching for locking; Npgsql's effective SQL target is authoritative for translation, while actual server/extensions are separate probe facts. The library exposes a caller-invoked capability report, not a host startup/readiness lifecycle. Host owners decide when to call it and how to gate service.

**Минимум общей инфраструктуры:** единая регистрация там, где заменяются provider services; узкая compatibility/diagnostics support; общего базового `IPostgreSqlOperation`, собственных DI/container/transaction managers или комплексного SQL AST нет. Повторяющиеся части становятся общими по доказанному совпадению семантики.

**Admission constraints:** design selection and source presence do not establish supported execution or a compatibility range. Row-lock execution validates the original expression/model, final SQL structure and command-time transaction/fulfillment. Missing registration, service replacement, a selection-bound mismatch or unsupported shape fails explicitly; a marked query must never execute successfully without the requested lock. Evidence requirements for exact query, DML, clock and consumer behavior are defined in the [first-production contract](briefs/production-contract.md). Package ID ownership, publication, SQL-target reconfiguration and broader compatibility claims remain outside this architecture baseline.
