# Начало работы

В этом руководстве мы создадим консольное приложение: оно вставит отсутствующий документ, заблокирует строку, изменит содержимое и зафиксирует транзакцию. Используйте отдельную тестовую базу PostgreSQL: пример создаёт схему через `EnsureCreatedAsync`.

<a id="requirements"></a>
## Что понадобится

Нужны .NET 10 SDK, доступ к NuGet.org и работающая база PostgreSQL с разрешением создавать таблицы. Опорное сочетание версий — EF Core Relational `10.0.12`, Npgsql/provider `10.0.3` и PostgreSQL `16.14`; точные границы описаны в [разделе о совместимости](compatibility.md). Docker нужен для провайдерных тестов репозитория; для примера с уже работающим сервером PostgreSQL он необязателен.

<a id="get-package"></a>
## Соберите локальный пакет

Текущий предварительный пакет собирается из репозитория. Его доступность в публичном NuGet не подтверждена. В корне репозитория выполните:

```powershell
dotnet restore
dotnet pack src/EntityFrameworkCore.PostgreSQL.Extensions --configuration Release --output artifacts/packages
```

Архив библиотеки называется `EntityFrameworkCore.PostgreSQL.Extensions.0.1.0-preview.6.nupkg`. Создайте приложение в отдельном каталоге. Добавьте в него `Demo.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="EntityFrameworkCore.PostgreSQL.Extensions" Version="0.1.0-preview.6" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.12" />
  </ItemGroup>
</Project>
```

Рядом добавьте `NuGet.Config`, заменив `/absolute/path/to/repository` на путь к своей копии репозитория. Для Windows подойдёт, например, `D:/Projects/pgext/artifacts/packages`. Настройка источников действует только для этого примера:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="pgext-local" value="/absolute/path/to/repository/artifacts/packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

<a id="first-program"></a>
## Выполните пример

Сохраните полный код ниже в `Program.cs`. Строка подключения читается из `PGEXT_CONNECTION_STRING`. Храните учётные данные в окружении или хранилище секретов.

```csharp
using EntityFrameworkCore.PostgreSQL.Extensions;
using Microsoft.EntityFrameworkCore;

var connectionString = Environment.GetEnvironmentVariable("PGEXT_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set PGEXT_CONNECTION_STRING.");
var options = new DbContextOptionsBuilder<DemoDb>()
    .UseNpgsql(connectionString, npgsql =>
    {
        npgsql.SetPostgresVersion(14, 0);
        npgsql.UsePostgreSqlExtensions();
    })
    .Options;

await using var db = new DemoDb(options);
await db.Database.EnsureCreatedAsync();
var ct = CancellationToken.None;

await using var transaction = await db.Database.BeginTransactionAsync(ct);
var inserted = await db.InsertIgnoreAsync(
    new Document { Id = 1, Payload = "initial" }, x => x.Id, ct);
var document = await db.Documents
    .Where(x => x.Id == 1)
    .ForUpdate()
    .SingleAsync(ct);
document.Payload = "updated";
await db.SaveChangesAsync(ct);
await transaction.CommitAsync(ct);
Console.WriteLine($"Inserted: {inserted}; payload: {document.Payload}");

public sealed class Document
{
    public int Id { get; set; }
    public string Payload { get; set; } = "";
}

public sealed class DemoDb(DbContextOptions<DemoDb> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>(entity =>
        {
            entity.ToTable("Documents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Payload).IsRequired();
        });
        modelBuilder.MapClockTimestamp();
    }
}
```

Задайте `PGEXT_CONNECTION_STRING` средствами своей оболочки или конфигурации запуска, затем выполните:

```powershell
dotnet restore --configfile NuGet.Config
dotnet run --no-restore
```

Для пустой базы программа выведет `Inserted: True; payload: updated`. При повторном запуске — `Inserted: False; payload: updated`. `InsertIgnoreAsync` не меняет состояние отслеживания; последующий запрос загружает сущность, а `SaveChangesAsync` сохраняет изменение. Блокировка строки принадлежит созданной вами транзакции и действует до её завершения.

`SetPostgresVersion(14, 0)` выбирает целевую версию генерации SQL из опорной конфигурации; версия сервера при этом не меняется. Выбирайте свой SQL target осознанно и прочитайте [руководство по настройке и диагностике](capabilities.md).

<a id="next-steps"></a>
## Что дальше

Остальные примеры в руководствах используют эти `DemoDb`, `Document` и токен отмены `ct`; это фрагменты для методов вашего приложения. Для схемы существующего приложения используйте миграции. Вызов `EnsureCreatedAsync` предназначен для этого отдельного примера и не обновляет существующую схему.

Перед усложнением запроса с блокировкой прочитайте [руководство по блокировкам](query-integration.md). Особенности вставки, серверного времени и режима только для чтения описаны в [разделе «Данные и транзакции»](internal-mechanisms.md).
