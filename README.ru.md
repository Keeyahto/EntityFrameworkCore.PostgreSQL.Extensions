# EntityFrameworkCore.PostgreSQL.Extensions

[English](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/README.md)

Типизированные операции PostgreSQL для Entity Framework Core и Npgsql:

- **Блокировки:** блокировка строк через LINQ и advisory-блокировки на время транзакции.
- **Данные и транзакции:** атомарная вставка одной строки с пропуском выбранного конфликта, время PostgreSQL и транзакции только для чтения.
- **Настройка и диагностика:** явная проверка целевой версии SQL, версии сервера и необходимых расширений базы данных.

Один пакет для `net10.0`, одно пространство имён: `EntityFrameworkCore.PostgreSQL.Extensions`. Текущая версия — **0.1.0-preview.6**. Доступность пакета в публичном NuGet не подтверждена; в [начале работы](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/docs/ru/getting-started.md) описана локальная установка. API предварительной версии может меняться.

## Пример

Для настроенного набора сущностей `Records` и существующей строки зарегистрируйте поддержку блокировок и выполните работу в своей транзакции:

```csharp
using EntityFrameworkCore.PostgreSQL.Extensions;
using Microsoft.EntityFrameworkCore;

options.UseNpgsql(connectionString,
    npgsql => npgsql.UsePostgreSqlExtensions());

await using var transaction = await db.Database.BeginTransactionAsync(ct);
var record = await db.Records
    .Where(x => x.Id == id)
    .ForUpdate(LockWait.NoWait)
    .SingleAsync(ct);

record.Payload = "updated";
await db.SaveChangesAsync(ct);
await transaction.CommitAsync(ct);
```

Строка остаётся заблокированной до завершения транзакции. Запрос с неподдерживаемой формой и маркером блокировки завершается явной ошибкой. Поддерживается ограниченный набор запросов к корневой сущности; JOIN, проекции, Include и глобальные фильтры исключены. Полные условия и поведение при конкуренции описаны в [руководстве по блокировкам](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/docs/ru/query-integration.md).

Приложение управляет транзакциями, отменой и повторными попытками. Остальные семейства операций можно использовать независимо от регистрации блокировок строк.

## Документация

Читайте [документацию на русском](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/docs/ru/index.md) или [English documentation](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/docs/index.md). Руководства объединяют связанные операции и содержат примеры, ограничения и диагностику.

Опорное сочетание версий: EF Core Relational `10.0.12`, Npgsql/provider `10.0.3`, PostgreSQL `16.14` и целевая версия SQL Npgsql `14.0`. Перед выбором других версий прочитайте [раздел о совместимости](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/docs/ru/compatibility.md).

Сборка, тестирование и локальный запуск сайта описаны в [Contributing](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/CONTRIBUTING.md). Лицензия — [Apache-2.0](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/LICENSE).
