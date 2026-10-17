# EntityFrameworkCore.PostgreSQL.Extensions — архитектура публичной библиотеки

This repository contains an experimental PoC for one PostgreSQL-specific EF Core/Npgsql library. The four integration boundaries and their constraints are the design baseline. Bounded local PostgreSQL checks cover selected query, DML, and scalar-function forms. The production query carrier, public API scope, compatibility range, and NuGet publication have not been admitted; detailed verification evidence belongs with the change record.

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

The public repository is independently buildable and contains one runtime project and one test project. It uses Apache-2.0; its upstream is `https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions`. NuGet package ID availability and ownership remain unconfirmed. Keep one packable project and one public assembly; do not add microprojects for conceptual layers. Use internal feature components and expose only the EF-facing API. A separate query-plugin package requires demonstrated provider-internal or plugin compatibility need.

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

The current target is `net10.0` with `global.json` requesting SDK `10.0.100`, `rollForward=latestFeature`, and prereleases disabled. The current resolution is SDK 10.0.401. TFM and SDK remain separate compatibility axes. CI uses a stable .NET 10 SDK; build, SourceLink and package claims require their own evidence.

The current central package pins are Npgsql EF provider `10.0.3`, Npgsql `10.0.3`, and EF Core Relational `10.0.12`; the integration fixture pins PostgreSQL `16.14`. These are PoC inputs, not a support matrix. EF Core 8/9/11, other PostgreSQL majors and additional package revisions require separate evidence before being claimed. Library package version remains independent of EF version.

- Dev/build зависимости выбираются через один `Directory.Packages.props`; test libraries `PrivateAssets` там, где должны оставаться dev-only.
- Публичный NuGet-dependency должен объявлять **минимально проверенную** Npgsql EF provider версию и проверенную compatible major. Upper bound `< 11.0.0` может быть обоснован реальной несовместимостью internal API; нельзя фиксировать exact patch для downstream consumer. Ограничение до major должно быть объяснено в `docs/compatibility.md`; Microsoft по умолчанию предостерегает от upper bounds без причины.
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

The experimental query registration requires an explicit comparison carrier: `options.UseNpgsql(cs, npgsql => npgsql.UsePostgreSqlExtensions(PocCarrier.ExpressionTag));`. This setting selects a probe, not a production winner. Pure `DatabaseFacade` helpers need no query registration. Idempotence, service replacement conflicts, pooling/factory composition and NodaTime/NetTopologySuite coexistence remain proof obligations.

### D1. Row-level locking (experimental source API)

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

Source exposes `ForUpdate`, `ForShare`, and `WithRowLock(LockStrength, LockWait)` for the unambiguous root entity. A physical lock requires an active caller-owned transaction and remains until it completes. `LockWait` provides wait, no-wait, and skip-locked options. The generator controls the alias structurally; callers cannot provide raw table names. The API remains experimental: unsupported shapes must fail, and `OrderBy` is not proof of a deterministic lock acquisition order across concurrent updates.

Required shape matrix includes SELECT entity/selection, tracking/no-tracking, joins (including LEFT nullable-side), `Take`, `Select` and generated aliases, `Distinct`, GroupBy, UNION, subquery, split queries, compiled queries and provider pooling. Unsupported cases fail **before silently issuing unlocked query**. PostgreSQL `FOR UPDATE` for nullable join side has restrictions; do not promise universal JOIN support. Transaction check must run at the statement execution boundary, not at query declaration.

**Current PoC implementation:** three structural carriers are available for comparison: typed `TableExpression` annotation with QMT visitor and SQL generator (A), QMT-processed reserved query tag and generator (B), and query-expression-interceptor reserved tag and generator (C). All depend on provider internals and need real-provider proof. No carrier is selected for production. A command interceptor may validate active-transaction and fulfillment conditions; it must never rewrite SQL text to add a locking clause.

### D2. Advisory locks (experimental source API)

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);
await db.Database.AcquireTransactionAdvisoryLockAsync(reference, ct);
// caller-owned protected work
await tx.CommitAsync(ct);
```

One active PostgreSQL transaction, exact `pg_advisory_xact_lock(hashtextextended(@text, 0))` behavior for application's existing path. This is a PostgreSQL-side hash, **not** .NET `GetHashCode()`. Other primary typed keys are 64-bit and 2×int32; they live in separate PostgreSQL key spaces. No session-scoped lock fallback. Caller selects stable key identity and relation to domain. Default API waits; optional try-lock later on proven use case. Library executes under current DbContext connection/current EF transaction, releases nothing manually, does not open/commit transaction. No request-level retry/collision handling.

### D3. Atomic `ON CONFLICT DO NOTHING` (experimental source API)

```csharp
var inserted = await db.InsertIgnoreAsync(
    new ReservationRow { ... },
    conflictTarget: x => new { x.ResourceId, x.ReservationKey }, ct);
// inserted == true exactly when INSERT affected a row
```

The experimental `InsertIgnoreAsync` API accepts one entity and a property selector for a mapped primary/unique key. It returns whether PostgreSQL inserted a row. Keep its supported mappings narrow; filtered unique indexes, unsupported generated values, and unrelated constraint conflicts must not be silently broadened. It does not implement `DO UPDATE` or check-then-insert. Mapping, tracking and concurrency guarantees require provider evidence.

### D4. Database time/functions (experimental source API)

```csharp
var rows = await db.Leases
    .Where(x => x.ExpiresAt > DatabaseClock.ClockTimestamp())
    .ToListAsync(ct);
```

Call `modelBuilder.MapClockTimestamp()` while building the model to map this database-only function.

`clock_timestamp()` is wall time at evaluation; PostgreSQL `now()` is transaction start. `MapClockTimestamp()` maps `DatabaseClock.ClockTimestamp()` as a model `DbFunction`; direct client invocation throws. There is no .NET client-time fallback. Repeated function calls may return different values within one SQL statement, so a one-value requirement needs an explicit query shape. Current provider checks exercise `timestamptz`/UTC mapping and precision for the tested form; broader public support remains subject to maintainer review.

The experimental `DatabaseFacade.SetTransactionReadOnlyAsync` helper issues native `SET TRANSACTION READ ONLY` in the caller's current transaction. PostgreSQL enforces its timing and write restrictions. Future capabilities include PostgreSQL SQLSTATE classification (technical facts only, without owner outcomes), `ON CONFLICT DO UPDATE`, and array/JSONB operators after confirming Npgsql support.

## E. Cross-cutting correctness contracts

| Condition | Behavior |
|---|---|
| Wrong database provider | Deterministic explicit unsupported-provider failure; never best-effort execute or silently return no-op |
| Unsupported LINQ query shape | Explicit translation/rejection; never unlock a query by ignoring a lock marker |
| Missing explicit transaction for advisory/row locks | Explicit failure before semantics are claimed; transaction helper never silently creates one |
| Caller cancellation | Forward `CancellationToken`; do not swallow or retry; PostgreSQL's uncertain commit remains caller/owner responsibility |
| Multiple plugin registrations | Idempotent or fail loudly; never silently replace another provider service for the whole DbContext |
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
- Current GitHub Actions configuration: Linux restore, Release build, full tests and pack; Windows restore, Release build and unit tests. These workflows are configured checks; remote CI execution has not been recorded. A later compatibility sweep can cover additional PostgreSQL/SDK versions. Do not run an expensive all-version Cartesian product on every PR unless necessary.

## G. Developer experience / documentation / open-source readiness

- Before release, README should state purpose, install instructions, verified support, a working example, warnings/limitations and feature-doc links.
- Per-feature docs: API examples, SQL generated, transaction expectations, supported/unsupported query shapes, multi-session examples, EF query tracking and parameter security.
- Docs build from real compiler-tested snippets if inexpensive; at minimum samples compile and are run in CI. XML docs on all exported symbols; IntelliSense usage is primary documentation surface.
- `LICENSE`: Apache-2.0 is present in the public repository. Check third-party license terms and preserve required attribution before copying any code.
- `CONTRIBUTING.md`: one short developer setup and review rules; `SECURITY.md`: non-public reporting instructions; `CHANGELOG.md`: meaningful user-facing change history. No issue template zoo or website generator until contributors appear.
- NuGet metadata: `PackageId`, `PackageDescription`, version, author, SPDX license, repository/source URL, embedded README, Source Link/symbol package, deterministic build; pack-and-inspect gate.
- SemVer for public package, preview release before v1.0.0; no commitment to preserve unstable API beyond stated preview policy. Automatic NuGet release on tagged signed/approved release with GitHub OIDC **Trusted Publishing** (when NuGet owner/policy configured). Never put long-lived NuGet API keys in CI by default.
- Release notes tie plugin behavior to EF/Npgsql tested versions; dependency update PRs run integration tests. Popularity comes from robust documented capabilities and a trustable release pipeline, not badges/boilerplate count.

## H. Architecture and pending decision register

| Key | Candidate / requested action | Status |
|---|---|---|
| LIB-01 | `EntityFrameworkCore.PostgreSQL.Extensions` naming, no application domain dependencies, four initial capability families | Current design baseline |
| LIB-02 | Public Git repository and independent build/docs | Repository and Apache-2.0 license are present; release readiness remains open |
| LIB-03 | One NuGet package / one runtime assembly + internal capability folders | Implemented as the current project topology; NuGet ID availability remains unknown |
| LIB-04 | `net10.0`, SDK 10.0.100/latestFeature; EF Core 10/Npgsql 10 baseline | Current project pins are documented above; no support range is established |
| LIB-05 | One public namespace; provider opt-in only for features that need it | API and registration are experimental |
| LIB-06 | Structural query generation, never command-text lock rewriting | A/B/C carriers are implemented as probes; no winner selected |
| LIB-07 | Admit public behavior only after query, DML and clock semantics are proven | Bounded local PostgreSQL checks cover selected forms; production API admission remains subject to maintainer review |
| LIB-08 | Apache-2.0; SemVer and trusted publishing for a future release | License is present; package identity and publication remain open |
| LIB-09 | Independent library build, documentation and contribution workflow | Standalone setup and contribution instructions are present; local package construction and remote CI remain unverified |
| LIB-10 | No unsupported multi-TFM, NativeAOT, EF11 or generic bulk/extended DSL claims | These remain outside current experimental scope |


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

**Зачем:** библиотека может опираться на внутренние EF Core/Npgsql SQL generator API, поэтому компиляция самой библиотеки против одного pinned patch не доказывает работу уже собранного NuGet-пакета с другой 10.x revision. Для public claims различать: (1) `.NET TFM`, (2) EF/Npgsql provider packages, (3) PostgreSQL server major, (4) публичный API самой библиотеки. Каждая ось имеет собственный oracle.

**Минимальный, без собственного framework:**

1. For production package admission, run `build → unit → real PostgreSQL integration → dotnet pack → isolated consumer smoke` with the selected versions. The smoke installs the local `.nupkg` without `ProjectReference`, covers basic configuration, SQL generation, unsupported/fail-closed cases and one end-to-end transaction scenario per admitted feature. The current PoC CI ends at pack; it does not establish this consumer gate.
2. Release evidence follows the explicitly adopted support contract. If it declares a minimum dependency revision and a newer tested revision, run the same small consumer smoke for both boundary sets within the selected 10.x line; if `min == current`, one set suffices. Adding a second declared version is a separate support-policy choice. The PoC does not adopt a two-version policy, choose a minimum, or resolve an implicit "latest" version. Do not run every EF × SDK × PostgreSQL combination in each PR.
3. PostgreSQL server: полные concurrency/integration tests для текущей reference version, дополнительная server version только при намерении **объявить её поддерживаемой**; release-support table в `docs/compatibility.md` содержит лишь проверенные комбинации. Не обещать untested PostgreSQL major versions.
4. Обновления: dependabot NuGet PR с разумной периодичностью (например еженедельно) и grouping patch updates, пропускать их через основной CI. Никакого самописного «latest version resolver», custom compatibility test runner, realtime watcher или огромной динамической matrix.
5. Public library API: use standard `EnablePackageValidation`; compare against the prior stable NuGet API after the first stable release. Do not build a custom ABI or SemVer comparator.
6. Dependency policy: в собственной сборке/test matrix версии выбираются явно для воспроизводимости. Public `.nuspec` dependency minimum должен соответствовать реально проверенной версии; exact consumer patch pins не навязывать. Upper bound следующей EF/Npgsql major — **только по доказанному incompatibility или явной version support policy**, с учётом рекомендации NuGet избегать неаргументированных upper bounds.

### Что сознательно не добавляем

- Второй совместимый TFM, поддержку EF 8/9/11, тестовую матрицу всех исторических версий PostgreSQL, NativeAOT/trimming claims без конкретного потребителя.
- Собственный analyzer применения библиотеки, «CompatibilityRegistry», генератор compatibility-документов или отдельный проект для матрицы.
- Негарантируемую поддержку query shapes с fallback до unlocked SQL.
- Фиксацию SDK patch `rollForward=disable`; the current `10.0.100/latestFeature` request allows feature-band roll-forward while build/evidence record the resolved SDK.

The isolated packed-consumer smoke is required before claiming package usability. Declared support determines the version sets that require evidence under item 2. Matrix expansion beyond those claims and routine dependency-update automation remain optional maintenance recommendations.

**Official verification references:**

- https://learn.microsoft.com/en-us/dotnet/fundamentals/package-validation/overview
- https://learn.microsoft.com/en-us/nuget/concepts/package-versioning
- https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files
- https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/configure-version-updates


## K. Current mechanism and admission status

The four independent technical integration mechanisms are the design baseline. They are not four projects, required interfaces, an operation framework, or a closed taxonomy.

| Mechanism | Класс задачи | Предпочтительная точка интеграции | Статус реализации |
|---|---|---|---|
| Query Integration | Row-level locking | Structural EF/Npgsql query pipeline and SQL generation | A/B/C carriers have bounded local PostgreSQL checks; no production carrier or supported-shape set is selected |
| Scalar Expression Integration | PostgreSQL functions such as `clock_timestamp()` | Native `DbFunction` or additive translator where needed | Clock mapping has bounded provider checks; public admission remains open |
| Command Execution | Transaction-scoped advisory locks | `DatabaseFacade` command under the caller's existing transaction | The experimental helper has bounded PostgreSQL checks; public admission remains open |
| Typed DML Construction | `INSERT ... ON CONFLICT DO NOTHING` | Typed values, EF metadata and parameterized PostgreSQL DML | The experimental helper has bounded PostgreSQL checks; public admission remains open |

**Cross-cutting guarantees:** no silent unlocked query; no implicit transaction/commit/retry; no domain/application dependency; no raw SQL text patching for locking; Npgsql's effective SQL target is authoritative for translation, while actual server/extensions are separate probe facts. The library exposes a caller-invoked capability report, not a host startup/readiness lifecycle. Host owners decide when to call it and how to gate service.

**Минимум общей инфраструктуры:** единая регистрация там, где заменяются provider services; узкая compatibility/diagnostics support; общего базового `IPostgreSqlOperation`, собственных DI/container/transaction managers или комплексного SQL AST нет. Повторяющиеся части становятся общими по доказанному совпадению семантики.

**Admission constraints:** implementation and successful compilation do not alone select a carrier. `SelectExpression` in EF10 is sealed and its `WithAnnotations` throws; `TableExpression` annotations and `IQueryExpressionInterceptor` remain experimental carrier choices. `ForUpdate()` must fail closed when the extension is missing or a shape is unsupported; it must never silently produce an unlocked result.

The current code is a PoC, not a production feature. No API signature or carrier becomes stable by appearing in source. Production admission requires exact supported and rejected shapes, correct alias/scope behavior, real PostgreSQL concurrency evidence, registration/cache/plugin composition checks, and maintainer approval. See [PoC plan](poc-plan.md).

NuGet package availability, stable API status, package publication, SQL-target reconfiguration, broader feature obligations, and a compatibility framework are not established by this repository state.
