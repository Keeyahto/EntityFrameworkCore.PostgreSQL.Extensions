using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using EntityFrameworkCore.PostgreSQL.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using Testcontainers.PostgreSql;

internal static class Program
{
    private const string ImageReference = "postgres:16-alpine@sha256:33f923b05f64ca54ac4401c01126a6b92afe839a0aa0a52bc5aeb5cc958e5f20";
    private const string ResultPrefix = "PGEXT_PACKAGE_CONSUMER_RESULT:";
    private static readonly List<string> Checks = new();

    public static async Task<int> Main()
    {
        try
        {
            await using var container = new PostgreSqlBuilder(ImageReference)
                .WithDatabase("pgext_consumer")
                .WithUsername("pgext_consumer")
                .WithPassword($"consumer-{Guid.NewGuid():N}-{Guid.NewGuid():N}")
                .Build();
            await container.StartAsync();
            var connectionString = container.GetConnectionString();

            await RunRegistrationCompositionAsync(connectionString);
            await RunNodaTimeCompositionAsync(connectionString);
            await RunProviderSmokeAsync(connectionString);
            await RunDefaultSchemaInsertIgnoreAsync(connectionString);
            await RunSmallIntegerRowLockAsync(connectionString);

            var assembly = typeof(InsertIgnoreExtensions).Assembly;
            var assemblyPath = Path.GetFullPath(assembly.Location);
            var expectedPackages = Path.GetFullPath(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? throw new InvalidOperationException("The isolated NuGet packages directory was not configured."));
            var expectedVersion = Environment.GetEnvironmentVariable("PGEXT_EXPECTED_PACKAGE_VERSION")
                ?? throw new InvalidOperationException("The expected package version was not configured.");
            var assemblyHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assemblyPath))).ToLowerInvariant();
            var cachedAssemblyPath = Path.Combine(expectedPackages,
                "entityframeworkcore.postgresql.extensions", expectedVersion.ToLowerInvariant(),
                "lib", "net10.0", "EntityFrameworkCore.PostgreSQL.Extensions.dll");
            var cachedAssemblyHash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(cachedAssemblyPath))).ToLowerInvariant();
            var loadedFromExpectedPackage = assemblyHash == cachedAssemblyHash
                && Path.GetDirectoryName(assemblyPath) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            Require(loadedFromExpectedPackage, "The loaded extension assembly does not match the isolated exact-version package output.");
            Console.WriteLine(ResultPrefix + JsonSerializer.Serialize(new
            {
                status = "passed",
                checks = Checks,
                loadedFromExpectedPackage,
                loadedAssemblySha256 = assemblyHash
            }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(ResultPrefix + JsonSerializer.Serialize(new
            {
                status = "failed",
                checks = Checks,
                failureType = exception.GetType().Name,
                failureSite = new System.Diagnostics.StackTrace(exception).GetFrames()
                    .Take(5).Select(frame => frame.GetMethod()?.DeclaringType?.Name + "." + frame.GetMethod()?.Name)
            }));
            return 1;
        }
    }

    private static async Task RunRegistrationCompositionAsync(string connectionString)
    {
        var rowId = 7001;
        var dbContextObserver = new SelectObserver();
        var dbContextServices = new ServiceCollection();
        dbContextServices.AddDbContext<SmokeContext>(options => Configure(options, connectionString, dbContextObserver, repeatedRegistration: true));
        await using var dbContextProvider = dbContextServices.BuildServiceProvider();
        await using (var scope = dbContextProvider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SmokeContext>();
            await context.Database.EnsureCreatedAsync();
            context.Records.Add(NewRecord(rowId, "registration-seed"));
            await context.SaveChangesAsync();
        }

        dbContextObserver.Clear();
        await using (var scope = dbContextProvider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SmokeContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var locked = await context.Records.Where(row => row.Id == rowId).ForUpdate().SingleAsync();
            Require(locked.Id == rowId, "AddDbContext repeated registration did not lock the expected row.");
            await transaction.RollbackAsync();
        }
        Require(dbContextObserver.Commands.Any(command => command.Contains("FOR UPDATE OF", StringComparison.Ordinal)), "AddDbContext did not execute a registered row lock.");

        var factoryServices = new ServiceCollection();
        factoryServices.AddDbContextFactory<SmokeContext>(options => Configure(options, connectionString));
        await using var factoryProvider = factoryServices.BuildServiceProvider();
        var factory = factoryProvider.GetRequiredService<IDbContextFactory<SmokeContext>>();
        await using (var context = await factory.CreateDbContextAsync())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var locked = await context.Records.Where(row => row.Id == rowId).ForShare(LockWait.NoWait).SingleAsync();
            Require(locked.Id == rowId, "AddDbContextFactory did not execute the share-lock form.");
            await transaction.RollbackAsync();
        }

        var poolObserver = new SelectObserver();
        var poolServices = new ServiceCollection();
        poolServices.AddDbContextPool<SmokeContext>(options => Configure(options, connectionString, poolObserver), poolSize: 1);
        await using var poolProvider = poolServices.BuildServiceProvider();
        SmokeContext? pooledContextInstance = null;
        await using (var scope = poolProvider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SmokeContext>();
            pooledContextInstance = context;
            await using var transaction = await context.Database.BeginTransactionAsync();
            _ = await context.Records.Where(row => row.Id == rowId).ForUpdate().SingleAsync();
            await transaction.RollbackAsync();
        }
        await using (var scope = poolProvider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SmokeContext>();
            Require(ReferenceEquals(pooledContextInstance, context), "AddDbContextPool did not reuse its size-one context instance.");
            _ = await context.Records.Where(row => row.Id == rowId).SingleAsync();
        }
        Require(poolObserver.Commands.Count == 2, "AddDbContextPool did not execute the two expected queries.");
        Require(!poolObserver.Commands[1].Contains("FOR UPDATE", StringComparison.Ordinal), "AddDbContextPool reused a lock intent for an ordinary query.");

        var pooledFactoryObserver = new SelectObserver();
        var pooledFactoryServices = new ServiceCollection();
        pooledFactoryServices.AddPooledDbContextFactory<SmokeContext>(options => Configure(options, connectionString, pooledFactoryObserver), poolSize: 1);
        await using var pooledFactoryProvider = pooledFactoryServices.BuildServiceProvider();
        var pooledFactory = pooledFactoryProvider.GetRequiredService<IDbContextFactory<SmokeContext>>();
        SmokeContext? pooledFactoryContextInstance = null;
        await using (var context = await pooledFactory.CreateDbContextAsync())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            pooledFactoryContextInstance = context;
            _ = await context.Records.Where(row => row.Id == rowId).ForUpdate().SingleAsync();
            await transaction.RollbackAsync();
        }
        await using (var context = await pooledFactory.CreateDbContextAsync())
        {
            Require(ReferenceEquals(pooledFactoryContextInstance, context), "AddPooledDbContextFactory did not reuse its size-one context instance.");
            _ = await context.Records.Where(row => row.Id == rowId).SingleAsync();
        }
        Require(pooledFactoryObserver.Commands.Count == 2, "AddPooledDbContextFactory did not execute both expected queries.");
        Require(!pooledFactoryObserver.Commands[1].Contains("FOR UPDATE", StringComparison.Ordinal), "AddPooledDbContextFactory reused a lock intent for an ordinary query.");
        Checks.Add("registration:add-db-context-factory-pool-repeated");
    }

    private static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder options,
        string connectionString,
        SelectObserver? observer = null,
        bool repeatedRegistration = false)
    {
        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UsePostgreSqlExtensions();
            if (repeatedRegistration)
            {
                npgsql.UsePostgreSqlExtensions();
            }

            npgsql.UseNodaTime();
        });
        if (observer is not null)
        {
            options.AddInterceptors(observer);
        }

        return options;
    }

    private static async Task RunNodaTimeCompositionAsync(string connectionString)
    {
        var value = Instant.FromUtc(2026, 10, 17, 8, 30, 12).PlusNanoseconds(123_000);
        var forwardOptions = new DbContextOptionsBuilder<SmokeContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseNodaTime();
                npgsql.UsePostgreSqlExtensions();
            })
            .Options;
        await using (var context = new SmokeContext(forwardOptions))
        {
            await context.Database.EnsureCreatedAsync();
            var entity = new PluginRecord { Id = 8001, ObservedAt = value };
            context.PluginRecords.Add(entity);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            Require((await context.PluginRecords.SingleAsync(row => row.Id == entity.Id)).ObservedAt == value,
                "NodaTime did not round-trip with NodaTime registered before the library extension.");
        }

        var reverseOptions = new DbContextOptionsBuilder<SmokeContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UsePostgreSqlExtensions();
                npgsql.UseNodaTime();
            })
            .Options;
        await using (var context = new SmokeContext(reverseOptions))
        {
            var entity = new PluginRecord { Id = 8002, ObservedAt = value + Duration.FromSeconds(1) };
            context.PluginRecords.Add(entity);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            Require((await context.PluginRecords.SingleAsync(row => row.Id == entity.Id)).ObservedAt == entity.ObservedAt,
                "NodaTime did not round-trip with the library extension registered before NodaTime.");
        }

        Checks.Add("registration:nodatime-both-orders-native-roundtrip");
    }

    private static async Task RunProviderSmokeAsync(string connectionString)
    {
        var observer = new SelectObserver();
        var options = new DbContextOptionsBuilder<SmokeContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UsePostgreSqlExtensions();
                npgsql.UseNodaTime();
            })
            .AddInterceptors(observer)
            .Options;

        await using var context = new SmokeContext(options);
        await context.Database.EnsureCreatedAsync();
        var target = NewRecord(9001, $"target-{Guid.NewGuid():N}");
        target.SecondaryKey = $"secondary-target-{Guid.NewGuid():N}";
        var nonTarget = NewRecord(9002, $"nontarget-{Guid.NewGuid():N}");
        nonTarget.SecondaryKey = $"secondary-nontarget-{Guid.NewGuid():N}";
        context.Records.AddRange(target, nonTarget);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await VerifyRowLockScopeAsync(context, connectionString, target.Id, nonTarget.Id);
        await VerifyUnsupportedShapesFailClosedAsync(connectionString, target.Id);
        await VerifyAdvisoryLockSeedAsync(context, connectionString);
        await VerifyInsertIgnoreAsync(context, connectionString);
        await VerifyClockAsync(context);
        await VerifyReadOnlyAndProbeAsync(context);
        Checks.Add("provider:real-postgresql-contract");
    }

    private static async Task RunDefaultSchemaInsertIgnoreAsync(string connectionString)
    {
        const string schema = "package_consumer_default_schema";
        var options = new DbContextOptionsBuilder<DefaultSchemaInsertIgnoreContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var context = new DefaultSchemaInsertIgnoreContext(options);
        await context.Database.ExecuteSqlRawAsync($"""
            CREATE SCHEMA IF NOT EXISTS "{schema}";
            DROP TABLE IF EXISTS "{schema}"."DefaultSchemaInsertIgnoreRows";
            DROP TABLE IF EXISTS public."DefaultSchemaInsertIgnoreRows";
            CREATE TABLE "{schema}"."DefaultSchemaInsertIgnoreRows" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "Key" text NOT NULL UNIQUE,
                "Payload" text NOT NULL);
            CREATE TABLE public."DefaultSchemaInsertIgnoreRows" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "Key" text NOT NULL UNIQUE,
                "Payload" text NOT NULL);
            """);

        var entityType = context.Model.FindEntityType(typeof(DefaultSchemaInsertIgnoreRow))!;
        Require(entityType.GetSchema() == schema, "The default-schema entity did not retain its table schema.");
        Require(entityType.GetViewName() is null, "The default-schema entity unexpectedly has a view mapping.");
        Require(entityType.GetViewSchema() == schema, "The runtime model did not expose the default schema through view metadata.");

        Require(await context.InsertIgnoreAsync(
            new DefaultSchemaInsertIgnoreRow { Key = "selected-conflict", Payload = "stored-payload" },
            row => row.Key), "InsertIgnoreAsync did not insert the default-schema row.");
        Require(!await context.InsertIgnoreAsync(
            new DefaultSchemaInsertIgnoreRow { Key = "selected-conflict", Payload = "ignored-payload" },
            row => row.Key), "InsertIgnoreAsync did not ignore the selected default-schema conflict.");
        var stored = await context.Rows.AsNoTracking().SingleAsync(row => row.Key == "selected-conflict");
        Require(stored.Payload == "stored-payload", "The default-schema selected conflict changed the stored payload.");
        Require(await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM public.\"DefaultSchemaInsertIgnoreRows\"").SingleAsync() == 0,
            "InsertIgnoreAsync targeted the same-named public table instead of the default-schema table.");
        Require(context.ChangeTracker.Entries().Count() == 0, "InsertIgnoreAsync changed tracking after the selected conflict.");

        var rollbackKey = "rollback-default-schema";
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Require(await context.InsertIgnoreAsync(
                new DefaultSchemaInsertIgnoreRow { Key = rollbackKey, Payload = "transactional-payload" },
                row => row.Key), "InsertIgnoreAsync did not insert within the caller transaction.");
            Require(ReferenceEquals(transaction, context.Database.CurrentTransaction),
                "InsertIgnoreAsync changed the caller transaction.");
            Require(context.ChangeTracker.Entries().Count() == 0, "InsertIgnoreAsync tracked the transactional entity.");
            Require(await context.Rows.AnyAsync(row => row.Key == rollbackKey),
                "The inserted default-schema row was not visible in its caller transaction.");
            await transaction.RollbackAsync();
        }

        Require(!await context.Rows.AnyAsync(row => row.Key == rollbackKey),
            "The caller rollback did not remove the default-schema row.");
        Require(context.ChangeTracker.Entries().Count() == 0, "The rolled-back insert changed tracking state.");
        Checks.Add("insert-ignore:default-schema-selected-conflict-payload-rollback-tracker");
    }

    private static async Task RunSmallIntegerRowLockAsync(string connectionString)
    {
        const string schema = "package_consumer_short_key";
        var observer = new SelectObserver();
        var options = new DbContextOptionsBuilder<SmallIntegerLockContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UsePostgreSqlExtensions())
            .AddInterceptors(observer)
            .Options;

        await using var context = new SmallIntegerLockContext(options);
        await context.Database.ExecuteSqlRawAsync($"""
            CREATE SCHEMA IF NOT EXISTS "{schema}";
            DROP TABLE IF EXISTS "{schema}"."ShortKeyRows";
            CREATE TABLE "{schema}"."ShortKeyRows" (
                "ShortKey" smallint PRIMARY KEY,
                "Payload" text NOT NULL);
            INSERT INTO "{schema}"."ShortKeyRows" ("ShortKey", "Payload") VALUES (1, 'target'), (2, 'other');
            """);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var promotedKey = 1;
            var selected = await context.Rows.Where(row => row.ShortKey == promotedKey).ForUpdate().SingleAsync();
            Require(selected.ShortKey == 1, "The promoted short-key predicate selected a different row.");
            Require(observer.Commands.Count == 1
                && observer.Commands[0].Contains("FOR UPDATE OF", StringComparison.Ordinal),
                "The short-key query did not execute the structural row lock.");
            Require(!await TryNativeShortKeyLockAsync(connectionString, 1),
                "PostgreSQL allowed another transaction to lock the selected short-key row.");
            Require(await TryNativeShortKeyLockAsync(connectionString, 2),
                "The short-key row lock affected a non-target row.");
            await transaction.RollbackAsync();
        }

        Checks.Add("row-lock:promoted-smallint-key-target-only");
    }

    private static async Task<bool> TryNativeShortKeyLockAsync(string connectionString, short key)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM \"package_consumer_short_key\".\"ShortKeyRows\" WHERE \"ShortKey\" = @key FOR UPDATE NOWAIT";
            command.Parameters.AddWithValue("key", key);
            var selected = await command.ExecuteScalarAsync();
            await transaction.CommitAsync();
            return selected is not null;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await transaction.RollbackAsync();
            return false;
        }
    }

    private static async Task VerifyRowLockScopeAsync(
        SmokeContext context,
        string connectionString,
        int targetId,
        int nonTargetId)
    {
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var selected = await context.Records.Where(row => row.Id == targetId).ForUpdate().SingleAsync();
            Require(selected.Id == targetId, "The row-lock query returned a different target.");

            var compiled = EF.CompileQuery((SmokeContext db, int id) =>
                db.Records.Where(row => row.Id == id).ForUpdate().Single());
            Require(compiled(context, targetId).Id == targetId, "The compiled row-lock query returned a different target.");

            Require(!await TryNativeNowaitLockAsync(connectionString, targetId), "The selected row was not locked by the caller transaction.");
            Require(await TryNativeNowaitLockAsync(connectionString, nonTargetId), "The row lock affected a non-target row.");
            await transaction.RollbackAsync();
        }

        Checks.Add("row-lock:target-only-compiled-update-and-share-modes");
    }

    private static async Task<bool> TryNativeNowaitLockAsync(string connectionString, int id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM package_consumer.\"SmokeRecords\" WHERE \"Id\" = @id FOR UPDATE NOWAIT";
            command.Parameters.AddWithValue("id", id);
            var found = await command.ExecuteScalarAsync();
            await transaction.CommitAsync();
            return found is not null;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await transaction.RollbackAsync();
            return false;
        }
    }

    private static async Task VerifyUnsupportedShapesFailClosedAsync(string connectionString, int id)
    {
        var shapeObserver = new SelectObserver();
        var options = new DbContextOptionsBuilder<SmokeContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UsePostgreSqlExtensions();
                npgsql.UseNodaTime();
            })
            .AddInterceptors(shapeObserver)
            .Options;
        await using (var context = new SmokeContext(options))
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var rejected = false;
            try
            {
                _ = await context.Records.Where(row => row.Rank > 0).ForUpdate().SingleAsync();
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("predicates may contain only direct mapped scalar equality and AND", StringComparison.Ordinal))
            {
                rejected = true;
            }

            Require(rejected, "An unsupported non-equality predicate was accepted for row locking.");
            Require(shapeObserver.SelectCount == 0, "The unsupported predicate issued a SELECT before rejection.");
            await transaction.RollbackAsync();
        }

        var missingRegistrationObserver = new SelectObserver();
        var missingRegistrationOptions = new DbContextOptionsBuilder<SmokeContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseNodaTime())
            .AddInterceptors(missingRegistrationObserver)
            .Options;
        await using (var context = new SmokeContext(missingRegistrationOptions))
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var rejected = false;
            try
            {
                _ = await context.Records.Where(row => row.Id == id).ForUpdate().SingleAsync();
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("could not be translated", StringComparison.Ordinal)
                && exception.Message.Contains("ForUpdate", StringComparison.Ordinal))
            {
                rejected = true;
            }

            Require(rejected, "A row-lock query without extension registration was accepted.");
            Require(missingRegistrationObserver.SelectCount == 0, "A query without extension registration issued a SELECT.");
            await transaction.RollbackAsync();
        }

        Checks.Add("row-lock:unsupported-predicate-and-missing-registration-no-select");
    }

    private static async Task VerifyAdvisoryLockSeedAsync(SmokeContext context, string connectionString)
    {
        var key = $"package-consumer:{Guid.NewGuid():N}";
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.AcquireTransactionAdvisoryLockAsync(key);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var competitorTransaction = await connection.BeginTransactionAsync();
        Require(!await TryTextLockAsync(connection, competitorTransaction, key, 0), "The server-side seed-zero text lock was not held by the library call.");
        Require(await TryTextLockAsync(connection, competitorTransaction, key, 1), "The text advisory lock did not preserve the server-side hash seed.");
        await competitorTransaction.RollbackAsync();
        await transaction.RollbackAsync();
        Checks.Add("advisory-lock:server-hashtextextended-seed-zero");
    }

    private static async Task<bool> TryTextLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, int seed)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_try_advisory_xact_lock(hashtextextended(@key, @seed))";
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("seed", seed);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task VerifyInsertIgnoreAsync(SmokeContext context, string connectionString)
    {
        var trackedBefore = context.ChangeTracker.Entries().Select(entry => (entry.Entity, entry.State)).ToArray();
        var uniqueKey = $"insert-target-{Guid.NewGuid():N}";
        var secondaryKey = $"secondary-{Guid.NewGuid():N}";
        Require(await context.InsertIgnoreAsync(NewRecord(0, uniqueKey, secondaryKey), row => row.UniqueKey), "The insert-ignore winner did not insert one row.");
        Require(!await context.InsertIgnoreAsync(NewRecord(0, uniqueKey, $"other-secondary-{Guid.NewGuid():N}"), row => row.UniqueKey), "The selected duplicate target was not ignored.");
        Require(context.ChangeTracker.Entries().Select(entry => (entry.Entity, entry.State)).SequenceEqual(trackedBefore),
            "InsertIgnoreAsync changed the EF change tracker.");

        var unrelatedFailedWithUniqueViolation = false;
        try
        {
            _ = await context.InsertIgnoreAsync(NewRecord(0, $"other-target-{Guid.NewGuid():N}", secondaryKey), row => row.UniqueKey);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            unrelatedFailedWithUniqueViolation = true;
        }
        Require(unrelatedFailedWithUniqueViolation, "An unrelated unique constraint was swallowed by insert-ignore.");

        var rollbackKey = $"rollback-{Guid.NewGuid():N}";
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Require(await context.InsertIgnoreAsync(NewRecord(0, rollbackKey, $"rollback-secondary-{Guid.NewGuid():N}"), row => row.UniqueKey),
                "The insert inside the caller transaction did not insert.");
            await using var observer = new NpgsqlConnection(connectionString);
            await observer.OpenAsync();
            await using var command = observer.CreateCommand();
            command.CommandText = "SELECT count(*) FROM package_consumer.\"SmokeRecords\" WHERE \"UniqueKey\" = @key";
            command.Parameters.AddWithValue("key", rollbackKey);
            Require(Convert.ToInt64(await command.ExecuteScalarAsync()) == 0, "The uncommitted insert was visible to another connection.");
            await transaction.RollbackAsync();
        }

        Require(!await context.Records.AnyAsync(row => row.UniqueKey == rollbackKey), "The caller rollback did not remove the inserted row.");
        Checks.Add("insert-ignore:selected-conflict-unrelated-constraint-tracker-rollback");
    }

    private static async Task VerifyClockAsync(SmokeContext context)
    {
        var sampled = await context.Database.ReadClockTimestampAsync();
        Require(sampled.Kind == DateTimeKind.Utc, "ReadClockTimestampAsync did not return UTC.");
        Require(sampled.Ticks % 10 == 0, "ReadClockTimestampAsync returned precision finer than PostgreSQL microseconds.");

        var mapped = await context.Records.Select(_ => DatabaseClock.ClockTimestamp()).FirstAsync();
        Require(mapped.Kind == DateTimeKind.Utc, "The mapped clock function did not return UTC.");
        Require(mapped.Ticks % 10 == 0, "The mapped clock function returned precision finer than PostgreSQL microseconds.");
        Checks.Add("clock:single-native-read-utc-microsecond-and-mapped-function");
    }

    private static async Task VerifyReadOnlyAndProbeAsync(SmokeContext context)
    {
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await context.Database.SetTransactionReadOnlyAsync();
            var writeRejected = false;
            try
            {
                var key = $"readonly-{Guid.NewGuid():N}";
                var secondary = $"readonly-secondary-{Guid.NewGuid():N}";
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO package_consumer.\"SmokeRecords\" (\"UniqueKey\", \"SecondaryKey\", \"Rank\", \"Payload\") VALUES ({key}, {secondary}, 0, 'readonly-probe')");
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ReadOnlySqlTransaction)
            {
                writeRejected = true;
            }

            Require(writeRejected, "SET TRANSACTION READ ONLY did not reject a write with PostgreSQL's native error.");
            await transaction.RollbackAsync();
        }

        var compatible = await PostgreSqlCapabilityProbe.CheckAsync(
            context,
            new[] { new PostgreSqlExtensionRequirement("plpgsql", "1.0") });
        Require(compatible.Outcome == PostgreSqlCapabilityOutcome.Compatible, "The capability probe did not report the installed required extension as compatible.");

        var missing = await PostgreSqlCapabilityProbe.CheckAsync(
            context,
            new[] { new PostgreSqlExtensionRequirement("pgext_package_consumer_missing_extension") });
        Require(missing.Outcome == PostgreSqlCapabilityOutcome.Incompatible, "The capability probe did not report a missing requirement as incompatible.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellationPropagated = false;
        try
        {
            _ = await PostgreSqlCapabilityProbe.CheckAsync(context, Array.Empty<PostgreSqlExtensionRequirement>(), cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationPropagated = true;
        }
        Require(cancellationPropagated, "The capability probe did not propagate caller cancellation.");
        Checks.Add("read-only:write-rejected;capability-probe:compatible-missing-cancelled");
    }

    private static SmokeRecord NewRecord(int id, string uniqueKey, string? secondaryKey = null)
        => new()
        {
            Id = id,
            UniqueKey = uniqueKey,
            SecondaryKey = secondaryKey ?? $"secondary-{Guid.NewGuid():N}",
            Rank = id,
            Payload = "package-consumer"
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class SmokeContext(DbContextOptions<SmokeContext> options) : DbContext(options)
{
    public DbSet<SmokeRecord> Records => Set<SmokeRecord>();
    public DbSet<PluginRecord> PluginRecords => Set<PluginRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SmokeRecord>(entity =>
        {
            entity.ToTable("SmokeRecords", "package_consumer");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.UniqueKey).IsRequired();
            entity.Property(row => row.SecondaryKey).IsRequired();
            entity.Property(row => row.Payload).IsRequired();
            entity.Property(row => row.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(row => row.UniqueKey).IsUnique();
            entity.HasIndex(row => row.SecondaryKey).IsUnique();
        });

        modelBuilder.Entity<PluginRecord>(entity =>
        {
            entity.ToTable("PluginRecords", "package_consumer");
            entity.HasKey(row => row.Id);
        });

        modelBuilder.MapClockTimestamp();
    }
}

internal sealed class DefaultSchemaInsertIgnoreContext(DbContextOptions<DefaultSchemaInsertIgnoreContext> options)
    : DbContext(options)
{
    public DbSet<DefaultSchemaInsertIgnoreRow> Rows => Set<DefaultSchemaInsertIgnoreRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("package_consumer_default_schema");
        modelBuilder.Entity<DefaultSchemaInsertIgnoreRow>(entity =>
        {
            entity.ToTable("DefaultSchemaInsertIgnoreRows");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.Key).IsUnique();
        });
    }
}

internal sealed class DefaultSchemaInsertIgnoreRow
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
}

internal sealed class SmallIntegerLockContext(DbContextOptions<SmallIntegerLockContext> options) : DbContext(options)
{
    public DbSet<SmallIntegerLockRow> Rows => Set<SmallIntegerLockRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SmallIntegerLockRow>(entity =>
        {
            entity.ToTable("ShortKeyRows", "package_consumer_short_key");
            entity.HasKey(row => row.ShortKey);
            entity.Property(row => row.ShortKey).HasColumnType("smallint");
        });
    }
}

internal sealed class SmallIntegerLockRow
{
    public short ShortKey { get; set; }
    public string Payload { get; set; } = string.Empty;
}

internal sealed class SmokeRecord
{
    public int Id { get; set; }
    public string UniqueKey { get; set; } = string.Empty;
    public string SecondaryKey { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

internal sealed class PluginRecord
{
    public int Id { get; set; }
    public Instant ObservedAt { get; set; }
}

internal sealed class SelectObserver : DbCommandInterceptor
{
    private int _selectCount;
    private readonly List<string> _commands = new();
    private readonly object _gate = new();

    public int SelectCount => Volatile.Read(ref _selectCount);

    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_gate)
            {
                return _commands.ToArray();
            }
        }
    }

    public void Clear()
    {
        Interlocked.Exchange(ref _selectCount, 0);
        lock (_gate)
        {
            _commands.Clear();
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command)
    {
        var sql = command.CommandText;
        if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _selectCount);
        }

        lock (_gate)
        {
            _commands.Add(sql);
        }
    }
}
