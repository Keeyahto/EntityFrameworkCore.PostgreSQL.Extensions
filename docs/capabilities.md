# EntityFrameworkCore.PostgreSQL.Extensions — Runtime Capability Discovery and Host Gate Design

The source implements `PostgreSqlCapabilityProbe.CheckAsync` as a read-only, caller-invoked probe. It reports Npgsql's configured SQL target, the connected server version, installed database extensions, and whether the caller-supplied extension requirements are met. It does not implement a host startup/readiness gate, infer requirements, set the SQL target, or install extensions. The exercised test configuration uses Npgsql SQL target 14 and a PostgreSQL 16.14 server; these values remain distinct. This runtime probe does not establish a library compatibility range or host lifecycle contract.

The broader startup/readiness design below remains a proposal and is not a library guarantee or public obligation.

## 1. Главный контракт и запреты

1. **Automatic discover-first:** фактическую версию текущего PostgreSQL получать из открытого `NpgsqlConnection.PostgreSqlVersion` (Npgsql ADO.NET public API). Состав **установленных в конкретной БД** расширений получать из PostgreSQL `pg_catalog.pg_extension` (`extname`, `extversion`, `extnamespace`); `pg_available_extensions` не доказывает установку. Сбор метаданных — read-only.
2. **Version semantics separated:** `ActualServerVersion` (от соединения), `EfSqlTargetVersion` (версия, для которой Npgsql EF Core компилирует SQL, обычно задаваемая `SetPostgresVersion` или provider default), `LibrarySupportedVersionRange` (проверенная совместимость данного NuGet package). **Не объявлять их взаимозаменяемыми.** Нельзя обещать, что EF Core самостоятельно пересоберёт сгенерированный SQL в ответ на обнаруженную более новую версию сервера.
3. **Requirements vs availability:** installed extension != required; приложение падает при отсутствующей/неподдерживаемой **требуемой** capability, но не из-за отсутствия произвольного, неиспользуемого расширения. Получение installed inventory не требует ручного списка. Требования должны следовать из штатной настройки Npgsql/EF provider или объявленных пользователем активированных библиотечных функций, если это можно доказать. Для случаев, не выводимых безошибочно, предоставить необязательный узкий explicit requirement (не дублировать большую матрицу конфигурации).
4. **No silent downgrade:** нельзя выполнять `ForUpdate()` без блокировки, подменять `DO NOTHING` на `Any()+Insert`, возвращать фальшивый успех или автоматически заменять семантически неэквивалентную функцию. При недоказанной пригодности — ошибка.
5. **Fail-before-serving for required incompatible capabilities:** если пользователь включил startup validation, endpoint и фоновые обработчики не должны работать до успешного прохождения проверки. Runtime library не является ASP.NET Host и не может гарантировать этот lifecycle только фактом установки пакета/вызова `UseNpgsql` — требуется одна явная host integration boundary. Default для **явного strict startup gate**: несовместимость и неопределённый результат проверки блокируют старт (после bounded timeout), с безопасной диагностикой.
6. **No forced side effects:** обнаружение/верификация не делает `CREATE EXTENSION`, `ALTER EXTENSION`, миграций, перезапусков или скрытого open/close/transaction для каждого LINQ-запроса. Не требует подключения к БД при design-time model build, migration generation, `dotnet pack` или регистрации DI.

## 2. Нормативная техническая проблема — момент определения версии

`NpgsqlConnection.PostgreSqlVersion` доступно **после** открытия соединения. А `NpgsqlDbContextOptionsBuilder.SetPostgresVersion` управляет SQL target provider **в момент настройки контекста**; Npgsql может по умолчанию предполагать определённую версию вместо подключения к БД. EF кеширует model/query compilation по config/query shape. Следовательно, стратегия «прочитать версию при первом LINQ-вызове и на её основе менять SQL generator» запрещена в базовом дизайне: недостоверные cache keys, compiled queries, DbContext pooling, разные серверы при failover.

**Два допустимых сценария (не выдавать за готовый API):**

- **Safe baseline proposal:** configure Npgsql without a duplicated SQL target in this library. The caller may invoke the capability probe at an explicit host boundary. The probe does not require the row-lock query-carrier registration. Translation remains governed by Npgsql's configured target and never switches silently to the actual server version.
- **Optional advanced AutoTarget bootstrap (PoC):** приложение асинхронно открывает Npgsql data source, получает actual PostgreSQL version и required extension snapshot **до построения неизменяемых DbContextOptions/provider query-services**; затем Npgsql конфигурируется штатным `SetPostgresVersion(discoveredVersion)` либо меньшим явно выбранным compatibility target. Обязателен PoC coexistence с `AddDbContext`, pooling, `AddDbContextFactory`, design-time EF migrations, immutable per-data-source options и несколькими DBs. Не реализовывать свой альтернативный Npgsql version setting; helper только передаёт обнаруженное значение в штатный provider. Если избежать сложной асинхронной composition без undue ceremony не удастся — оставить безопасную baseline-модель и отложить AutoTarget.

**Внешние ограничения:** PostgreSQL extension metadata не гарантирует доступность функции для данной роли/search_path; расширения могут иметь не-SemVer версии; installed extension не означает автоматически включённый Npgsql plugin (например, spatial type mapping требует явной регистрации). Приложение может подключаться к разным физическим узлам через failover/pooling. Успешный probe одного узла — доказательство лишь проверенной connection/database в данный момент, не всей потенциальной topology.

## 3. Startup lifecycle: варианты и рекомендация

| Стадия | Действие | Fault behaviour |
|---|---|---|
| 0. Static/Design-time | Build EF options/model, но без обязательного сетевого probe | Нет подключения к БД |
| 1. Schema provisioning | Независимые EF migrations/`CREATE EXTENSION` под управлением host/deployment, если предусмотрены | Библиотека ничего не создаёт; порядок для app в зоне ответственности host |
| 2. Validate / discover | Открыть bounded Npgsql connection; версии из Npgsql, установленные extensions/versions из pg catalog; validate declared capabilities, provider target, supported version range | Mismatch = deterministic incompatibility; timeout/permission/connectivity/undecidable = indeterminate (не success) |
| 3. Gate | Strict startup path выполняется **до `app.Run`/публикации готовности**, или host `IHostedService` с доказанным blocking ordering | Default Strict: fail startup after bounded probe/retry; optional host readiness waiting policy как другой контракт |
| 4. Normal queries | Использовать стабильный provider configuration + verified capability snapshot; fail-loud при unsupported запросе | Никаких авто-fallback/retry/probe на каждый запрос |
| 5. Upgrade/failover | Обновление базы требует repeat validation/restart/config recreation если затронуло SQL target/extension registration | Health/revalidation optional; startup snapshot не вечная гарантия |

Предпочтение владельца «при старте приложение падает при недоступных требуемых механизмах» выражается **Fail-Closed strict host gate**. Для временной сетевой недоступности предложены две возможные host-level политики: (A) fail process после bounded попыток (по умолчанию strict), (B) процесс жив, но `Readiness=Unhealthy` и retry пока не verified. Подмена B на A автоматически в любой инфраструктуре не всегда выгодна (Kubernetes restart loops); базовая библиотека должна различать `Incompatible` и `Indeterminate`, а не сама предписывать один процесс-менеджмент всем пользователям.

**Startup gate != all tests of installed extensions:** требовать только те capabilities, которыми действительно обусловлена корректность включённых features. Невозможно надёжно реконструировать все будущие LINQ method invocations по IL или DI registration. Когда потребность не следует из деклараций, её нужно один раз указать через `Require...`/`Enable...` configuration, после чего проверка реальной базы автоматическая. Отдельный Npgsql extension, который уже объявлен в EF Model (`HasPostgresExtension`) может быть использован как declared requirement **только после PoC обоснованной интерпретации и migration ordering**.

## 4. Минимальный internal shape, без нового framework

**Internal immutable probe snapshot:** actual PG Version + database identity/connection source + installed extensions (names, opaque version strings, optional schema), validation timestamp для diagnostics, scoped connection source; не mutable global singleton. Требования — ограниченная коллекция (feature id, required/optional, provider applicability). Не создавать user-facing `CapabilityRegistry`, per-request negotiator, EF migration engine, package-specific IHostedService зависимость без реального host consumer.

**Возможный внешне понятный API (НЕ финальная сигнатура):**

```csharp
// Capability discovery is independent of experimental query-carrier registration.
options.UseNpgsql(connectionString);

// Host выполняет один явный pre-serve gate, который сам получает фактическую
// PostgreSQL version и installed extension inventory; это НЕ второй version option.
await app.ValidatePostgreSqlExtensionsAsync(ct); // placeholder: форма API требует PoC
```

Если функция PostgreSQL extension реально нужна, кандидат на одну декларацию `RequireExtension("pg_trgm")` допустим (происходит **проверка**, а не попытка установить её). Не нужно требовать от потребителя перечислять все установленные plugins.

## 5. PoC/readiness obligations (до API freeze)

1. **Source truth:** PG16/17/18 и Npgsql `NpgsqlConnection.PostgreSqlVersion`; Npgsql EF target и provider default; explicit lower vs higher version, behavior in logs/compiled-query caches.
2. **Version bootstrap:** real usable `AutoTarget` before EF options, `AddDbContextPool/Factory`, migrations/design-time, multiple distinct connections; если сложно без чрезмерной ceremony, не вводить обязательный механизм.
3. **Installed extensions:** базовая БД без extension; отсутствующий required `pg_trgm`; установленный `pg_trgm` с экстеншн версией и non-default schema; installed-but-unusable due to role/schema privileges; unknown/extversion opaque string; no false installed from `pg_available_extensions`.
4. **Startup:** app does not serve endpoint/background job until verified; deterministic mismatch fails, network outage timed-out policy distinct, cancellation and security diagnostics without revealing connection credentials.
5. **Migrations:** extension created by migrations doesn't cause pre-migration false-negative gate; offline EF tooling continues without DB connection; no implicit database mutation by library.
6. **Multi-DB & failover:** each configured database validated separately; heterogenous backend/role on failover leads to fail-loud/recheck; snapshot not falsely global.
7. **Safety:** unsupported feature never quietly translated as no-op; no re-probe on every query; no mutable global state controlling EF SQL translator.

## 6. Implementation and open host decisions

The current API returns `PostgreSqlCapabilityReport` with `Compatible`, `Incompatible`, or `Indeterminate` outcome. It does not start or gate a host, select a retry policy, infer requirements, or capture a durable per-datasource readiness snapshot. Callers own how and when to act on the report.

An explicit host validation boundary, strict failure versus readiness waiting, AutoTarget bootstrap, extension-requirement inference, and behavior under pool/replica/failover remain design questions. No host lifecycle API or startup guarantee is provided. Do not add a custom capability registry/DSL, dynamic per-query SQL-target switching, implicit extension installation, extra NuGet package, or app-wide background polling without a separately approved need.

### Sources

- Npgsql public `NpgsqlConnection.PostgreSqlVersion`: https://www.npgsql.org/doc/api/Npgsql.NpgsqlConnection.html
- Npgsql EF provider configuration `SetPostgresVersion`: https://www.npgsql.org/efcore/api/Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder.html
- Npgsql EF default target in internal `NpgsqlOptionsExtension` (unstable): https://github.com/npgsql/efcore.pg/blob/main/src/EFCore.PG/Infrastructure/Internal/NpgsqlOptionsExtension.cs
- Npgsql EF10 release notes and version-conditional translations: https://www.npgsql.org/efcore/release-notes/10.0.html
- PostgreSQL installed extension catalog: https://www.postgresql.org/docs/current/catalog-pg-extension.html
- PostgreSQL available extension list: https://www.postgresql.org/docs/current/view-pg-available-extensions.html
- EF Core query/model caching: https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics
- ASP.NET readiness vs liveness: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks
