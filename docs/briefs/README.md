# EntityFrameworkCore.PostgreSQL.Extensions — технические задания

Эта папка содержит самостоятельные задания публичной библиотеки. Они пригодны для передачи разработчику, имеющему доступ только к её репозиторию. Здесь отсутствуют продуктовые контракты потребителей.

- [PoC](PGEXT-POC.md): сравнение query carriers и проверка остальных механизмов, результат GO/NARROW/HOLD.
- [Production](PGEXT-PRODUCTION.md): допущенные возможности, unit/integration tests, packed consumer и техническая документация.
- [First-production contract](production-contract.md): approved carrier, API, supported/rejected shapes and compatibility target; implementation and package evidence remain separate.

Repository identity and Apache-2.0 license are established for this public PoC checkout. Package ID availability, publication and stable API status remain open. Current inputs are one runtime assembly, .NET 10 SDK 10.0.401, EF Core Relational 10.0.12, Npgsql EF Core/Npgsql 10.0.3, and a PostgreSQL 16.14 Testcontainers fixture. Bounded local PostgreSQL checks cover selected API forms; they do not establish production admission or a compatibility range.

Архитектурные договорённости, матрицы тестов и способы локального build/test должны быть доступны в этом же repository. Публикация repository/package выполняется по отдельному разрешению с реальными учётными правами; локальная подготовка кода не означает успешную публикацию.
