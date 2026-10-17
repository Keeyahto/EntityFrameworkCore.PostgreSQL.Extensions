# PGEXT-PoC — структурная интеграция PostgreSQL-возможностей с EF Core

Repository source contains the experimental PoC implementations. Bounded local PostgreSQL checks cover selected root-lock, ordering/limit and compiled-query forms across the three carriers; specific tested Q05–Q12 relational cases reject before issuing a SELECT where required. Arbitrary unmarked-predicate subqueries and global query filters remain uncharacterized. This brief defines evidence needed to compare the carriers and propose a production scope; it does not select an implementation. Detailed run results belong with change evidence.

## 1. Фиксированные границы

One packable library project and one runtime assembly. Current inputs are .NET 10 with SDK request 10.0.100/`latestFeature` resolving to 10.0.401, Npgsql EF provider 10.0.3, Npgsql 10.0.3, EF Core Relational 10.0.12, and a Testcontainers fixture pinned to PostgreSQL 16.14. Npgsql SQL target 14 and a separate target-15 context smoke check remain distinct test inputs. Apache-2.0 and the upstream GitHub repository are established; NuGet package ID availability and publication remain undecided. Other versions require evidence before being claimed.

Четыре технических механизма находятся в одной библиотеке:

1. Whole-query Integration — row locks и точный SQL FROM occurrence.
2. Scalar Expression Integration — функции БД, где штатного Npgsql API недостаточно.
3. Command Execution — операции в существующей транзакции через естественный `DatabaseFacade` API.
4. Typed DML Construction — узкая атомарная вставка из EF metadata и typed input.

Не создавать свой ORM, generic Repository/UoW, universal operation framework, общий SQL AST/compiler или несколько микропакетов. Четыре механизма не являются закрытым навсегда каталогом и не требуют четырёх public interfaces.

Сохранять привычный EF стиль: один namespace `EntityFrameworkCore.PostgreSQL.Extensions`, `IQueryable<T>`/`EF.Functions`/`DatabaseFacade`/`DbSet<T>` согласно семантике. Точные методы/регистрация — кандидаты до proof. SQL target задаёт Npgsql; подключённый сервер и требуемые installed extensions проверяются отдельно. Discovery не перенастраивает translation target и не выполняет CREATE EXTENSION.

## 2. Запреты корректности

- Запрошенная блокировка либо выполняется в правильном scope, либо запрос явно отклоняется. Нет successful unlocked fallback.
- Нет скрытых begin/commit/rollback/retry и session-scoped замены xact lock.
- Нет изменения готового SQL-текста для row locking.
- Нет `AsyncLocal`/global mutable состояния для передачи lock intent между запросами.
- Нет догадки об alias по одному CLR entity type, особенно self-join.
- Нет нового транзакционного/session manager; текущий DbContext/connection/transaction принадлежат вызывающему коду.
- Нет сторонних продуктовых данных, названий, приватных ссылок, credentials или абсолютных исходных путей в code/docs/history/package metadata.

## 3. Входной preflight

До первого public commit: утверждённая лицензия, реальный repository identity, clean public file/history scan. Не загружать чужие рабочие ZIP или внутренние инструкции. Внешний contributor должен восстановить setup по одному checkout.

Записать точные versions и dependency graph. Использовать настоящий Npgsql provider и EF tree, не самодельные классы-макеты. Отдельно сохранить native SQL/поведение при feature OFF. Разрешение начать исследование не означает, что перечисленные API провайдера реально совместимы.

## 4. Сравнить A/B/C на одном поведении

| Carrier | Минимальный технический вариант | Критический proof |
|---|---|---|
| A | Public `Expression.Call` marker; Npgsql QMT visitor; immutable intent на `TableExpression` occurrence; SQL generator | Аннотации переживают clone/remap/pruning и query caching; корректный subquery visitor. |
| B | Тот же public non-noop marker; QMT переводит проверенный intent в canonical internal tag; generator | Tag order/dedup независимы, wrong query scope/spoof/conflict отвергаются. |
| C | Public non-noop marker; `IQueryExpressionInterceptor` rewrite в strict internal tag; generator | Меньше замен provider services без потери extraction/cache/compiled query correctness. |

Не наследоваться от sealed `SelectExpression` и не считать его annotation API работающим без выполнения. Не делать публичный `ForUpdate` простой обёрткой над `TagWith`: при отсутствующем plugin он не должен стать no-op. Для каждого carrier достаточно минимального proof, не трёх production implementations. Конструкторы/фабрики Npgsql internals проверяются на реальной версии.

## 5. Обязательная Q01–Q18 матрица

| ID | Форма | Oracle |
|---|---|---|
| Q01 | Where(root id) → ForUpdate → SingleAsync | Lock root occurrence, `FOR UPDATE OF` корректного alias; одна выбранная строка. |
| Q02 | ForUpdate до Where | Proven same scope либо explicit unsupported; unlocked success запрещён. |
| Q03 | AsNoTracking + ForShare | Tracking не влияет на lock; правильный `OF`. |
| Q04 | OrderBy/Take(1) + lock | Cardinality/scope сохранены; порядок не объявляется абсолютным lock-order proof. |
| Q05 | JOIN | Блокируется заданный root occurrence, чужая JOIN строка остаётся свободной от нашего lock. |
| Q06 | LEFT JOIN nullable side | Невалидная nullable-side блокировка отвергается; target не выбирается молча. |
| Q07 | Self-join | Две occurrences одного CLR type различаются; при неоднозначности reject. |
| Q08 | DTO/scalar Select после lock | Исходный target сохранён либо unsupported до неверного запроса. |
| Q09 | Include/AsSplitQuery | Известен scope каждого SQL; иначе явный reject без частичной unlocked операции. |
| Q10 | Subquery/pushdown/OrderBy/Take | Lock остаётся в предусмотренном SELECT, не дублируется и не расширяется. |
| Q11 | Distinct/GroupBy/Count/Sum | При недоказанной row identity — unsupported, не удаление marker. |
| Q12 | Union/Except/Intersect | Не обещать поддержку; только proven special case либо явный reject. |
| Q13 | User TagWith + reserved spoof | User comments сохраняются, внутренний carrier не авторизуется произвольным текстом. |
| Q14 | Повторный identical/conflicting lock | Idempotent identical или обоснованный reject; противоречивые intent по умолчанию reject. |
| Q15 | CompileQuery/CompileAsyncQuery | Intent/cache-key безопасны; locked и unlocked не смешиваются. |
| Q16 | Captured/dynamic mode/target | Structural options не становятся случайными SQL parameters; prove segregation либо reject. |
| Q17 | Нет registration / другой provider / generator overwritten | Fail closed, никогда successful unlocked SELECT. |
| Q18 | Обычный запрос без lock | Native Npgsql SQL/result semantics без глобальной регрессии. |

Для каждого case зафиксировать фактический результат всех рассмотренных carriers: supported-pass, explicit-reject-as-designed, failure, not-executed. Последнее не заменяет required proof. Поддержку широкого shape не объявлять из одного string assertion.

## 6. Реальный PostgreSQL, минимум две сессии

Две разные connection/transaction, bounded synchronization/deadlines. Первая выполняет **сгенерированный** query, вторая пытается конфликтный UPDATE либо NOWAIT. Проверить release после commit/rollback, ожидание/отмену, unselected JOIN row, лимиты и изменение sort keys. Учитывать независимые FK locks, чтобы не приписать их расширению.

Проверить `FOR SHARE`, а дополнительные modes/NOWAIT/SKIP LOCKED — везде, где они рассматриваются для admitted scope. Missing tx проверяется **при выполнении**; построить expression до begin transaction допустимо. Borrowed `UseTransaction` проверяется отдельно; TransactionScope не считать поддержанным автоматически.

Проверять наблюдаемую блокировку/освобождение; `pg_locks` может быть вспомогательным свидетельством. Cancel waiting lock, kill connection, deadlock/serialization failure не должны превращаться в автоматический retry/no-op.

## 7. Registration/cache/pooling

AddDbContext/Factory/Pool, repeated opt-in, custom internal service provider, additive plugins и по необходимости NodaTime. Generator и interceptor wiring вместе: поздняя подмена части services не должна молча терять lock. Два context types/data sources и разные SQL targets; no cross-query residual state.

Записать точные проверенные EF/Npgsql packages. Минимальную и более новую совместимую 10.x пару из принятого query proof plan проверить, когда такие разные заявленные пары определены. Если min==current, это один набор; не утверждать проверку несуществующей «последней версии». Дальнейшая release-policy/dependency automation утверждается отдельно.

## 8. Проверить остальные механизмы в этом же PoC

| Механизм | Реальный proof | Ограничения |
|---|---|---|
| Advisory xact lock | `pg_advisory_xact_lock(hashtextextended(text,0))`, server hash/seed сохранены; int64 и два int32 имеют разные key spaces; blocking/release/borrowed tx/cancel | Никакого .NET GetHashCode, session fallback или lock manager. Проверить пригодность parameterized ExecuteSqlAsync для SELECT void; affected rows здесь не бизнес-результат. |
| Database clock | Найти native Npgsql support; при отсутствии — DbFunction/additive translator; отличить clock_timestamp от now в долгой tx | Нет client-time fallback; для created/expiry basis получать одно server value, не два разных volatile evaluation. |
| Atomic insert-ignore | Single mapped table; EF identifiers/converters/defaults и конкретный unique target; две конкурентные вставки, inserted/ignored | Без Any+Add, DO UPDATE, broad ignore прочих constraints, скрытого SaveChanges/tracker изменения. |
| Transaction read-only | Проверить минимальный native EF-oriented способ в caller-owned tx и допустимый момент применения | Если не включено в production scope, явно описать отсутствие support и не давать alias с другой семантикой. |
| Capability readiness | Actual server и enabled required extension verification после host migrations до начала требуемой работы | Не делать network probe на каждый LINQ query, не дублировать обязательную настройку version и не auto-install extension. |

Везде сначала штатный EF/Npgsql API. Собственный низкоуровневый command допустим только для доказанного missing mechanism и остаётся внутри соответствующей feature.

## 9. Выход и acceptance

Вернуть компилируемый public-neutral PoC, точный source commit/версии/DI composition, полную Q matrix, two-session logs, smoke остальных mechanisms и comparison correctness/сложность/поддержка. Independent review проверяет сырые результаты и сохраняет проваленные опыты.

- **GO:** выбран доказанный carrier, приемлемая поддержка, scope утверждён; production может использовать только подтверждённые capabilities.
- **NARROW:** узкая полезная форма доказана, остальное fail-closed; maintainer утверждает точный сокращённый contract.
- **HOLD:** безопасный Query carrier не доказан. Не использовать SQL text rewrite как аварийный fallback; production Query откладывается. Другие механизмы могут выпускаться отдельным согласованным reduced scope.

Документы/API status отражают evidence. Недоступная инфраструктура — not executed/blocked, без fake provider. В итоговом repository production path не содержит всех проигравших carriers; исторический эксперимент может сохраняться в Git history, не как несколько активных движков.
