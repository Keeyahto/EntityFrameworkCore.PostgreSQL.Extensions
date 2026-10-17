# SQL Integration PoC Gates — EntityFrameworkCore.PostgreSQL.Extensions

This file states proof gates for the current row-locking PoC. Source contains A/B/C carrier implementations and tests. Bounded local PostgreSQL checks exercise id-equality root locks, `OrderBy(...).Take(1)`, and synchronous/asynchronous compiled-query forms across all three carriers. Specific tested relational cases in Q05–Q12 reject before issuing a SELECT where required. Arbitrary unmarked-predicate subqueries and global query filters remain uncharacterized. Production carrier and supported-shape admission require maintainer review; detailed run outcomes belong with change evidence.

## E0 — Проверка компиляции и регистрационных швов

The project targets .NET 10 with the currently pinned EF Core/Npgsql package versions. Verify the implemented query visitor, SQL generator, options extension, interceptor lifetime, selected carrier, and Npgsql configured SQL target. Test NodaTime, context factory/pooling, and a competing replacement of provider services before production admission. Keep `EF1001` suppression within provider-specific integration code. Missing registration must fail rather than execute the query without a lock.

## E1 — Критический эксперимент: перенос намерения блокировки

Three implementations are available behind the same experimental API and must be compared against the same corpus:

- **A: Table-level typed annotation**. `Expression.Call` распознаётся Npgsql-derived query translator; immutable `PgLockIntent` размещается на корректных `TableExpression` (если допустимо); generator выводит clause из структурных таблиц/alias. Прямое `SelectExpression.AddAnnotation()` запрещено: в EF 10 `WithAnnotations` бросает `UnreachableException`. Проверить клонирование, quoting, SQL equality/hash/cache, `SetTables` до immutable stage.
- **B: QMT + TagWith carrier + structural generator**. Npgsql-derived query translator проверяет marker и переносит намерение в строгий закрытый служебный tag, без `AsyncLocal`; Npgsql-derived generator проверяет query shape и выводит SQL. Никакого переписывания `DbCommand.CommandText`. Проверить порядок/dedup, вложенные SELECT, пользовательские `TagWith` и кэширование.
- **C: Expression interceptor + TagWith carrier + structural generator**. Interceptor проверяет исходный expression и преобразует marker в canonical tag; native QMT сохраняется. Generator проверяет конечный SQL tree и target occurrence. Command interceptor только проверяет исполнение, без изменения SQL. Те же Q01–Q18 и runtime gates обязательны.

Форма SQL проверяется через `ToQueryString` **и реальные блокировки PostgreSQL в двух сессиях**. Само наличие подстроки `FOR UPDATE` не является success.

| Проверяемый класс | Позитивные и негативные случаи |
|---|---|
| Прямые запросы | `.Where().ForUpdate().SingleAsync()`, `.ForUpdate().Where()`, tracked и no tracking |
| JOIN | Inner, left outer, self join, несколько targets; `FOR UPDATE OF exact_alias`; nullable side rejection |
| Query transformations | Limit, Offset, OrderBy, projection, nested subquery, pushdown, Include, AsSplitQuery |
| Несовместимые shapes | Distinct, GroupBy, Union, Except, Intersect, aggregate, `Any`/`Count` при неизвестной корректности |
| Compiled queries | `EF.CompileQuery`, `EF.CompileAsyncQuery`, query-cache variations для нескольких lock modes |
| Transaction/runtime | Missing tx, borrowed EF transaction, commit/rollback, NOWAIT, SKIP LOCKED, cancel, disconnect |
| Plugins/DI | Registered only when enabled, two conflicting factories, AddDbContextPool/Factory, UseNodaTime |
| Версии | Минимальная проверенная Npgsql 10 patch и актуальная проверенная 10 patch; real PG16 и следующая объявленная поддерживаемая версия |

**Exit:** сравнить `A/B/C` по корректности композиции, сохранению target occurrence, реальному provider behavior и стоимости сопровождения. Результат — обоснованная рекомендация GO/NARROW/HOLD для maintainer review. Ни один кандидат не допускается, если он может тихо выполнить unlocked query. Точный общий corpus и порядок проверки находятся в [PoC plan](poc-plan.md).

## E2 — Runtime validation interceptor

`DbCommandInterceptor` validates the current borrowed EF transaction and checks that exactly one request and matching fulfillment receipt reach the command on the same connection. It also checks that the configured generator/translator services still match the selected carrier. These checks do not replace provider and concurrency proof. The interceptor never rewrites SQL; do not create hidden transactions/retries or add `SET LOCAL lock_timeout` without a separate scenario and test.

## E3 — Independent capabilities

- **Clock:** `IMethodCallTranslatorPlugin` либо narrow `DbFunction`. Проверить семантику `clock_timestamp()` против `now()` в долгой транзакции, UTC/value mapping и nullability; обычный SQL generator не менять.
- **Advisory:** `DatabaseFacade` под текущей EF транзакцией, серверный `pg_advisory_xact_lock(hashtextextended(@key,0))`, реальная конкуренция двух соединений, auto-release, cancel; без session lock.
- **Insert-ignore:** узкий metadata-driven `INSERT ... ON CONFLICT DO NOTHING`, model-mapped identifiers and values, параметризация, converters, unique index semantics, EF tracking policy, nullable/composite/shadow/generated keys в объёме доказанного. Реальный competing insert race, не `Any()+Add()`.

## E4 — Окончательное решение

Зафиксировать по результатам исполняемого PoC:

1. Настоящий carrier typed locking intent в EF 10 и доказанную семантику выбора target aliases.
2. Реально поддерживаемые и отклоняемые LINQ shapes, compiled queries и coexisting plugins.
3. Список заменяемых EF/Npgsql service factories, internal-version seam и регистрационную модель.
4. Доказанную matrix Npgsql patch/PG versions; не обещать неподтверждённые конфигурации.
5. Минимальный public API и список сознательно не поддерживаемых функций.

**Никто не должен объявлять PoC пройденным по факту компиляции, простого SQL-снапшота или одного последовательного запроса.** Риск семантической блокировки проверяется на реальных PostgreSQL конкурентных транзакциях и негативных случаях.
