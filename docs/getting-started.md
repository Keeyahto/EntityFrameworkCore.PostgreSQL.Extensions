# Getting started

This walkthrough creates a small console application that inserts a document if it is missing, locks it, changes its payload and commits. Use a disposable PostgreSQL database: the example creates its schema with `EnsureCreatedAsync`.

<a id="requirements"></a>
## Requirements

Use a .NET 10 SDK, access to NuGet.org and a running PostgreSQL database with permission to create tables. The reference set is EF Core Relational `10.0.12`, Npgsql/provider `10.0.3` and PostgreSQL `16.14`; see [Compatibility](compatibility.md) for the exact scope. Docker is needed for the repository's provider tests, and is optional when your example connects to an existing PostgreSQL server.

<a id="get-package"></a>
## Build a local package

The current preview package is built from this repository. Public NuGet availability has not been established. From the repository root:

```powershell
dotnet restore
dotnet pack src/EntityFrameworkCore.PostgreSQL.Extensions --configuration Release --output artifacts/packages
```

The runtime archive is `EntityFrameworkCore.PostgreSQL.Extensions.0.1.0-preview.4.nupkg`. Use a separate directory for your application. Create `Demo.csproj` there:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="EntityFrameworkCore.PostgreSQL.Extensions" Version="0.1.0-preview.4" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.12" />
  </ItemGroup>
</Project>
```

Add `NuGet.Config` beside it, replacing `/absolute/path/to/repository` with your checkout path. On Windows, a path such as `D:/Projects/pgext/artifacts/packages` is valid. This configuration keeps feed changes local to the example:

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
## Run the example

Save the following complete program as `Program.cs`. It reads the connection string from `PGEXT_CONNECTION_STRING`. Keep credentials in your environment or secret store.

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

Set `PGEXT_CONNECTION_STRING` using your shell or launch configuration, then run:

```powershell
dotnet restore --configfile NuGet.Config
dotnet run --no-restore
```

For an empty database, the program prints `Inserted: True; payload: updated`. Subsequent runs print `Inserted: False; payload: updated`. `InsertIgnoreAsync` leaves tracking untouched; the later query materializes the entity and `SaveChangesAsync` persists the change. The row lock belongs to the transaction you created and lasts until it completes.

`SetPostgresVersion(14, 0)` selects the SQL-generation target used by the reference setup; it does not change the actual server. Choose your own target deliberately and read [Configuration and diagnostics](capabilities.md).

<a id="next-steps"></a>
## Next steps

The remaining guide snippets use this `DemoDb`, `Document`, and a cancellation token named `ct`; they are fragments to place inside application methods. Use migrations for an existing application's schema. `EnsureCreatedAsync` is limited to this disposable example and does not upgrade an existing schema.

Read [Locking](query-integration.md) before composing a more complex locked query, or [Data and transactions](internal-mechanisms.md) for insert semantics, time access and read-only work.
