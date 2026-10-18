# Данные и транзакции

Здесь описаны атомарная вставка с пропуском конфликта, серверное время PostgreSQL и транзакции только для чтения. Операции вызываются у `DbContext` с провайдером Npgsql или его свойства `Database` и работают без `UsePostgreSqlExtensions()`. Примеры используют модель из [начала работы](getting-started.md).

| Метод | Результат |
|---|---|
| [InsertIgnoreAsync](#insert-api) | Признак того, была ли вставлена строка |
| [ReadClockTimestampAsync](#clock-api) | Одно значение серверных часов как UTC `DateTime` |
| [ReadClockExpiryAsync](#clock-expiry-api) | Общий серверный отсчёт и срок как UTC `DateTimeOffset` либо отсутствие представимого срока |
| [MapClockTimestamp / ClockTimestamp](#clock-mapping-api) | Настройка модели и выражение для серверных часов |
| [SetTransactionReadOnlyAsync](#read-only-api) | Режим только для чтения у текущей транзакции |

SQL соответствует модели примера и [опорным версиям](compatibility.md). `$1`, `$2` — параметры PostgreSQL; значения указаны рядом с примерами. Начало и фиксация транзакции в SQL не показаны. Запись параметров и сгенерированные псевдонимы могут меняться между версиями провайдера.

<a id="insert-api"></a>
## InsertIgnoreAsync

Вставляет одну отображённую сущность и пропускает только конфликт по выбранному вызывающим кодом уникальному ключу. В примере таким ключом служит `Document.Id`.

**C#**

```csharp
var candidate = new Document { Id = 1, Payload = "created" };
var inserted = await db.InsertIgnoreAsync(candidate, x => x.Id, ct);
```

**SQL** (`$1 = 1`, `$2 = "created"`):

```sql
INSERT INTO "Documents" ("Id", "Payload")
VALUES ($1, $2)
ON CONFLICT ("Id") DO NOTHING;
```

Метод отправляет один `INSERT ... ON CONFLICT (...) DO NOTHING` в PostgreSQL. Он возвращает `true`, если вставлена одна строка, и `false`, если вставку предотвратил выбранный уникальный конфликт. Значения существующей строки сохраняются. Нарушения остальных ограничений передаются как штатные ошибки PostgreSQL.

Публичная сигнатура:

```csharp
Task<bool> InsertIgnoreAsync<TEntity>(this DbContext context,
    TEntity entity, Expression<Func<TEntity, object?>> conflictTarget,
    CancellationToken cancellationToken = default)
    where TEntity : class;
```

Селектор указывает прямое отображённое свойство либо анонимный составной ключ, например `x => new { x.Code, x.Region }` для модели с такими свойствами. Столбцы должны соответствовать первичному ключу, альтернативному ключу или уникальному индексу без фильтра в модели. Частичные индексы, индексы по выражениям, вычисляемые селекторы, повторяющиеся свойства и произвольные конструкторы не поддерживаются. Для nullable-уникальности действуют правила PostgreSQL: NULL не превращается в равенство уровня приложения.

<a id="mapping-api"></a>
### Отображение, значения по умолчанию и отслеживание

Сущность должна отображаться на одну обычную таблицу, в том числе в схеме по умолчанию модели. Идентификаторы берутся из метаданных EF и экранируются провайдером; значения передаются параметрами с соответствующими отображениями типов и преобразователями. Представления, owned-сущности, наследование, complex/shadow-свойства, фрагменты таблиц/представлений и неоднозначное совместное использование таблицы отклоняются. Подготовка отображения, селектора, SQL и параметров завершается до открытия соединения.

Настроенный в EF sentinel обозначает пропуск значения для генерируемого свойства или свойства со значением по умолчанию. Пропущенное значение предоставляет PostgreSQL. Явный NULL можно отправить, только если он отличается от sentinel. Если у nullable-свойства sentinel равен NULL, API сущности не позволяет отдельно выразить и пропуск, и явный NULL. Неподдерживаемая генерация, включая явные значения identity-always и генерируемые базой значения при обновлении, отклоняется. Пользовательские defaults и преобразователи требуют проверки точного отображения на PostgreSQL.

`InsertIgnoreAsync` не присоединяет переданный экземпляр, не меняет состояние отслеживания, не вызывает `SaveChanges`, клиентские генераторы ключей или SaveChanges interceptors, не присваивает и не возвращает сгенерированные значения. Если нужна сохранённая сущность, прочитайте её отдельно. Уже отслеживаемая сущность остаётся под обычным управлением EF; последующий `SaveChanges` выполняет самостоятельную работу. Массовая вставка, `DO UPDATE`, общий upsert и `RETURNING` в этот API не входят.

<a id="atomicity-api"></a>
### Транзакция и атомарность

При наличии текущей присоединённой транзакции EF оператор участвует в ней. Иначе PostgreSQL выполняет его одним оператором с autocommit. Метод не создаёт транзакцию и не делает предварительный check-then-insert. Конкурентные вставки разрешаются уникальным ограничением PostgreSQL; конкурирующий оператор может ждать незавершённую вставку.

Чтобы включить вставку в более крупную работу, создайте транзакцию сами:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
var inserted = await db.InsertIgnoreAsync(
    new Document { Id = 1, Payload = "created" }, x => x.Id, ct);
await transaction.CommitAsync(ct);
```

<a id="clock-api"></a>
## ReadClockTimestampAsync

Для одного серверного значения используйте типизированный метод базы:

**C#**

```csharp
DateTime sampledAt = await db.Database.ReadClockTimestampAsync(ct);
```

**SQL**

```sql
SELECT pg_catalog.clock_timestamp();
```

Он один раз читает `pg_catalog.clock_timestamp()` и возвращает UTC `DateTime` с микросекундной точностью PostgreSQL. При наличии используется текущая присоединённая транзакция; без неё допускается самостоятельный оператор. Метод уравновешивает полученное им владение соединением, сохраняя ранее открытое соединение. Отмена и ошибки провайдера передаются дальше. Клиентские часы не подставляются.

Если нескольким значениям приложения нужен один момент времени, явно используйте полученный результат повторно. Для общего отсчёта и срока, вычисленного сервером, используйте следующий метод.

<a id="clock-expiry-api"></a>
## ReadClockExpiryAsync

`Task<(DateTimeOffset SampledAt, DateTimeOffset ExpiresAt)?> ReadClockExpiryAsync(this DatabaseFacade database, TimeSpan duration, CancellationToken cancellationToken = default)` получает один серверный отсчёт и вычисляет срок в том же операторе. Длительность должна быть положительной и содержать целое число миллисекунд. Неверная длительность приводит к `ArgumentOutOfRangeException` до выполнения команды. Поддерживаются длительности больше `int.MaxValue` миллисекунд.

```csharp
var expiry = await db.Database.ReadClockExpiryAsync(TimeSpan.FromDays(30), ct);
if (expiry is { } times)
{
    DateTimeOffset sampledAt = times.SampledAt;
    DateTimeOffset expiresAt = times.ExpiresAt;
}
```

Оба значения имеют нулевое смещение UTC и микросекундную точность PostgreSQL. Расчёт использует один вызов `pg_catalog.clock_timestamp()` и `duration_milliseconds * interval '1 millisecond'` с параметром типа `long`. Если срок превышает `9999-12-31 23:59:59.999999+00`, возвращается `null`; значение не ограничивается сверху и клиентские часы не подставляются. Верхняя граница задана SQL-литералом времени, поэтому преобразование бесконечности провайдером её не меняет. Ограничения TTL и политика lease принадлежат приложению.

**SQL** (`$1 = 2592000000`, тип `bigint`, для 30 дней; модель сущности не используется):

```sql
WITH sample AS MATERIALIZED (
    SELECT pg_catalog.clock_timestamp() AS sampled_at
), calculated AS (
    SELECT sampled_at,
           sampled_at + $1 * INTERVAL '1 millisecond' AS expires_at
    FROM sample
)
SELECT sampled_at,
       CASE
           WHEN expires_at > TIMESTAMPTZ '9999-12-31 23:59:59.999999+00' THEN NULL
           ELSE expires_at
       END AS expires_at
FROM calculated;
```

При наличии используется текущая присоединённая транзакция, иначе выполняется один самостоятельный оператор. Метод уравновешивает владение соединением EF и передаёт отмену и штатные ошибки провайдера. Он не создаёт транзакцию, ничего не записывает и не повторяет выполнение. При сохранении отсчёта и срока используйте возвращённую пару целиком.

<a id="clock-mapping-api"></a>
## MapClockTimestamp и DatabaseClock.ClockTimestamp

`MapClockTimestamp()` возвращает тот же `ModelBuilder` и настраивает обе функции часов. При регистрации SQL не выполняется. `DatabaseClock.ClockTimestamp()` представляет выражение типа `DateTime`, а `DatabaseClock.ClockTimestampOffset()` — типа `DateTimeOffset`. Обе функции вычисляют `pg_catalog.clock_timestamp()` на PostgreSQL внутри транслируемого запроса.

Зарегистрируйте отображение в `OnModelCreating`:

```csharp
modelBuilder.MapClockTimestamp();
```

Затем используйте функцию в обычном транслируемом запросе, например в проекции над непустой таблицей:

**C#**

```csharp
DateTime observedAt = await db.Documents
    .Select(x => DatabaseClock.ClockTimestamp())
    .FirstAsync(ct);
```

**SQL**

```sql
SELECT pg_catalog.clock_timestamp()
FROM "Documents" AS d
LIMIT 1;
```

Для `FirstAsync` нужна строка в `Documents`; пустая таблица приводит к его обычной ошибке отсутствия результата. Чтобы получить время независимо от содержимого таблицы, используйте [ReadClockTimestampAsync](#clock-api).

`DatabaseClock.ClockTimestamp()` отображается на изменяющееся текущее время PostgreSQL. Значение берётся при вычислении функции. PostgreSQL `now()` обозначает время начала транзакции. Повторные вызовы могут вернуть разные значения даже внутри одного оператора; отображение не гарантирует единственное общее вычисление. Прямой вызов в клиентском коде приводит к `NotSupportedException`.

Это обычные транслируемые выражения без маркера блокировки строк; ограничения проекций блокируемых запросов сохраняются. `ClockTimestampOffset()` допускает сравнение с отображёнными `DateTimeOffset` в предикате и присваивание обычному или nullable-столбцу времени через `ExecuteUpdate`. Каждое вхождение остаётся отдельным изменяющимся отсчётом, включая вхождения в предикате и присваивании. Общего отсчёта они не создают. Клиентский вызов любой функции приводит к `NotSupportedException`. Произвольные CTE/обновления и арифметика времени за пределами `ReadClockExpiryAsync` не получают дополнительных гарантий библиотеки.

<a id="read-only-api"></a>
## SetTransactionReadOnlyAsync

Включает режим только для чтения у текущей явной транзакции. Метод возвращает `Task`; ограничение обеспечивает PostgreSQL.

**C#**

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
await db.Database.SetTransactionReadOnlyAsync(ct);
var documents = await db.Documents.AsNoTracking().ToListAsync(ct);
await transaction.CommitAsync(ct);
```

**SQL-команда метода**

```sql
SET TRANSACTION READ ONLY;
```

Последующий `ToListAsync` — обычный запрос EF, который отдельно генерирует SELECT над `Documents`.

Вызывайте метод сразу после начала транзакции, до запросов, ограничивающих изменение её настроек в PostgreSQL. Он выполняет `SET TRANSACTION READ ONLY` в текущей присоединённой транзакции EF. PostgreSQL проверяет допустимый момент вызова и ограничения записи. Метод не меняет настройки сессии по умолчанию или уровень изоляции и не восстанавливает аварийную транзакцию.

<a id="ownership-api"></a>
## Владение ресурсами и ошибки

Все операции требуют EF-провайдер Npgsql и `NpgsqlConnection`. Заимствованная транзакция должна быть `NpgsqlTransaction`, присоединённой к этому соединению. Вставка с пропуском конфликта и чтение времени открывают и закрывают соединение через EF, сохраняя владение. Advisory-блокировки и команда только для чтения требуют существующую транзакцию.

Приложение управляет commit/rollback и восстановлением после отмены или штатных ошибок. Библиотека не добавляет автоматические повторы или классификацию предметных ошибок. Ошибки конфигурации и результаты проверки базы описаны в [руководстве по настройке и диагностике](capabilities.md).
