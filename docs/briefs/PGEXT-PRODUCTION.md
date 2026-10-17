# PGEXT-Production — первая самостоятельно используемая версия библиотеки

This brief specifies a future production-admission target. It does not establish that the current PoC is accepted, package-ready, or published. Production work requires maintainer approval of the API and exact supported scope.

The [proposed first-production contract](production-contract.md) specifies the concrete carrier, API/registration, supported/rejected shapes, mechanism limits and initial compatibility target for that approval. It remains a proposal; this brief retains the required implementation and verification outcome.

## 1. Preconditions

Принятый PoC GO либо явно утверждённый NARROW; точные Query carrier, supported/rejected shapes, registration и execution-time transaction contract. Для остальных mechanisms — proof native reuse/собственной реализации и current supported mappings. HOLD Query не даёт разрешения публиковать работающий locking API.

The repository identity and Apache-2.0 license are established. Maintainers still need to confirm package identity and the release test matrix. Preserve third-party attribution; do not copy material incompatible with the public library boundary or remove required notices. Keep private application names, links, dependencies, data and credentials out of source, docs, tests, examples and release artifacts. Use neutral test models such as Record, Lease, Resource and Job.

## 2. Required outcome

Самостоятельно собираемый package с точными public API, документацией и unit/real-PG/packed-consumer tests для:

- допущенных row-level locks (исходно ForUpdate/ForShare и только доказанные дополнительные формы);
- transaction-scoped advisory locks с точной key semantics;
- atomic single-row `ON CONFLICT DO NOTHING` с explicit conflict target;
- database clock/functions, если штатного API недостаточно;
- минимального transaction-read-only или иных уже доказанных дополнений, если они включены maintainer в scope.

Если scope сокращён после PoC, сначала изменить documented outcome и support table. Пустые заглушки/no-op/скрытый SQL fallback не считаются реализацией. Все четыре **способа интеграции** должны остаться соразмерными своим задачам, без универсального engine.

## 3. API и внутренности

Один namespace `EntityFrameworkCore.PostgreSQL.Extensions`; естественные receivers LINQ/EF.Functions/DatabaseFacade/DbContext. Public methods минимальны, обычное использование не требует отдельного application service/registry/DI everywhere.

Query plugin opt-in только где нужен. Commands через стандартный EF execution при доказанной эквивалентности. Scalar — additive translator/DbFunction при необходимости. DML — metadata identifiers+quoting/parameters/converters/defaults/unique target, без собственной полной SQL AST/ORM.

No hidden transaction begin/commit/retry, no general execution policy. Validation в правильной фазе: expression structure при translation, eligible current transaction при actual execution, required capabilities при соответствующем readiness boundary. No silent unlocked query и no ambient correctness state. Actual server verification не меняет Npgsql configured SQL target.

One-row insert: сохранять различия omitted default vs explicit null, generated values, typed key/converters и cardinality; unsupported mapping явно отказывать. Не игнорировать unrelated constraint violation. Tracking/SaveChanges/RETURNING политика явная; не обещать unsupported bulk/upsert.

## 4. Verification

| Категория | Обязательное evidence |
|---|---|
| Build/unit | Actual provider packages, API arguments, rejected shapes, no custom mock tree вместо EF. |
| Row locks | Q01–Q18 disposition + concurrency двух sessions для admitted scope; target/non-target/tx/release/cache/pooling/overwrite/no registration. |
| Advisory | Существующая и borrowed tx, same/different logical keys, exact server hash/seed, auto-release, отмена waiting lock. |
| DML | Concurrent insert one winner, correct affected rows, converters/defaults/nullability/composite keys, explicit wrong mapping/constraint rejection. |
| Clock/read-only | Volatile vs tx-start time, one evaluation когда требуется; correct tx read-only semantics и no client fallback. |
| Compatibility | Explicit EF/Npgsql/PG sets, selected plugin coexistence, Factory/Pool, supported dependency bounds объяснены evidence. |
| Package | `dotnet pack`; inspect nupkg/nuspec/XML docs/source/symbols/dependencies/license; отсутствие private links/paths. |
| External sample | Новая изолированная программа получает **собранный nupkg** из local feed, не ProjectReference, и выполняет реальные базовые сценарии всех included capabilities. |
| Independent review | Реальный diff и raw evidence, сохранённые failures и limitations; изменение API/support пересогласовано. |

Unsupported cases документируются и тестируются как deliberate failure. Не изобретать минимальный процент code coverage или all-version Cartesian matrix. Обычный CI: build/unit/integration/pack/consumer smoke; additional release matrix согласно фактически выбранным claims.

## 5. Публичные документы

README с назначением, минимальной установкой и рабочим примером, verified compatibility/limitations. `docs/architecture.md` для four mechanisms, per-feature docs, `docs/compatibility.md` с provider internal risk. CONTRIBUTING/AGENTS дают самостоятельные команды setup/build/test без внешнего частного corpus. XML docs для exported symbols; compiled samples и реальные snippets в CI.

LICENSE утверждён владельцем, CHANGELOG объясняет пользовательские изменения, SECURITY содержит реальные способы связи или корректный механизм reporting. Не выдумывать контакты/гарантированные SLA. Repository URL, authors, SourceLink и license metadata должны быть подлинными до release.

История публичного репозитория чиста с первого commit. Проверить не только исходные файлы, но archived fixtures, screenshots, generated docs, binlog/text logs для публикации, package metadata/symbols, PR/release text и Git history перед первым push. Secrets в public artifacts запрещены.

## 6. Приёмка и публикация

Вернуть library source commit, `.nupkg` version/hash, actual dependency/support matrix, tests и limitations. Потребитель может использовать exact artifact из проверенного feed. Публичный source repository и NuGet release — отдельные наблюдаемые события: отсутствие публикационного разрешения/учётных прав отражается явно.

Нельзя объявить package «опубликованным» после одного `dotnet pack`. Публикация на удалённый repository/NuGet выполняется только явно разрешённым действием. Release notes, tags и package связываются с проверенным source/evidence; credentials в код не добавляются.

## 7. Out of scope

Много TFM, все исторические PostgreSQL версии, EF8/9/11, NativeAOT/trimming claims без evidence, generic COPY/upsert/DDL framework, собственный analyzer suite или web docs engine, бизнес-ошибки и idempotency/recovery policy прикладных потребителей. Новые возможности добавляются по отдельно подтверждённой потребности.
