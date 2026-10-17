using System.Data;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Mechanisms;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
[Trait("MechanismTestCategory", "PostgreSQL")]
public sealed class InsertIgnoreMechanismTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task UsesIdentityAndDatabaseDefaultsWithoutChangingTracker()
    {
        await MechanismDmlDatabase.ResetAsync(fixture.ConnectionString);
        await using var context = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        var generated = new IdentityDefaultRow { UniqueKey = "generated", Payload = "one" };
        var capturedCreatedAt = DateTime.UtcNow.AddDays(-1);
        var explicitCreatedAt = new DateTime(capturedCreatedAt.Ticks / 10 * 10, DateTimeKind.Utc);
        var explicitIdentity = new IdentityDefaultRow
        {
            Id = 7001,
            UniqueKey = "explicit",
            Payload = "two",
            CreatedAt = explicitCreatedAt
        };

        Assert.True(await context.InsertIgnoreAsync(generated, row => row.UniqueKey));
        Assert.True(await context.InsertIgnoreAsync(explicitIdentity, row => row.UniqueKey));
        Assert.Empty(context.ChangeTracker.Entries());

        var tracked = await context.IdentityDefaults.SingleAsync(row => row.UniqueKey == "generated");
        tracked.Payload = "unsaved-tracker-value";
        var trackedEntry = context.Entry(tracked);
        Assert.Equal(EntityState.Modified, trackedEntry.State);
        Assert.True(await context.InsertIgnoreAsync(
            new IdentityDefaultRow { UniqueKey = "third", Payload = "independent-insert" },
            row => row.UniqueKey));
        Assert.Equal(EntityState.Modified, trackedEntry.State);
        Assert.Equal("unsaved-tracker-value", tracked.Payload);
        Assert.Equal(EntityState.Detached, context.Entry(explicitIdentity).State);

        var rows = await context.IdentityDefaults.AsNoTracking().ToDictionaryAsync(row => row.UniqueKey);
        Assert.Equal(3, rows.Count);
        Assert.True(rows["generated"].Id > 0);
        Assert.NotEqual(default, rows["generated"].CreatedAt);
        Assert.Equal("one", rows["generated"].Payload);
        Assert.Equal(7001, rows["explicit"].Id);
        Assert.Equal(explicitCreatedAt, rows["explicit"].CreatedAt);
    }

    [Fact]
    public async Task ParticipatesInCallerTransactionAndRollbackRemovesTheRow()
    {
        await MechanismDmlDatabase.ResetAsync(fixture.ConnectionString);
        await using var context = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        await using var observer = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var connection = context.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();
        const string key = "caller-rollback";

        Assert.True(await context.InsertIgnoreAsync(
            new IdentityDefaultRow { UniqueKey = key, Payload = "transactional" },
            row => row.UniqueKey));

        Assert.Same(transaction, context.Database.CurrentTransaction);
        Assert.Same(dbTransaction, context.Database.CurrentTransaction!.GetDbTransaction());
        Assert.Same(connection, context.Database.GetDbConnection());
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.True(await context.IdentityDefaults.AnyAsync(row => row.UniqueKey == key));
        Assert.False(await observer.IdentityDefaults.AnyAsync(row => row.UniqueKey == key));

        await transaction.RollbackAsync();

        Assert.False(await observer.IdentityDefaults.AnyAsync(row => row.UniqueKey == key));
    }

    [Fact]
    public async Task UsesCompositeUniqueTargetAtomicallyForConcurrentInsertions()
    {
        await MechanismDmlDatabase.ResetAsync(fixture.ConnectionString);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingCount = 0;
        async Task<bool> InsertAsync(string payload)
        {
            await using var context = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
            if (Interlocked.Increment(ref waitingCount) == 2)
            {
                ready.SetResult();
            }

            await release.Task;
            return await context.InsertIgnoreAsync(
                new CompositeUniqueRow { Left = "north", Right = "one", Payload = payload },
                row => new { row.Left, row.Right });
        }

        var first = InsertAsync("first");
        var second = InsertAsync("second");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        var inserted = await Task.WhenAll(first, second);

        Assert.Equal(1, inserted.Count(result => result));
        await using var verification = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        Assert.Equal(1, await verification.CompositeUnique.CountAsync());
    }

    [Fact]
    public async Task AppliesMappedValueConverterToDmlParameter()
    {
        await MechanismDmlDatabase.ResetAsync(fixture.ConnectionString);
        await using var context = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);

        Assert.True(await context.InsertIgnoreAsync(
            new ConvertedUniqueRow { Key = new ConvertedKey("converted-key"), Payload = "one" },
            row => row.Key));
        Assert.False(await context.InsertIgnoreAsync(
            new ConvertedUniqueRow { Key = new ConvertedKey("converted-key"), Payload = "duplicate" },
            row => row.Key));

        var row = await context.ConvertedUnique.SingleAsync(value => value.Key == new ConvertedKey("converted-key"));
        Assert.Equal("one", row.Payload);
    }

    [Fact]
    public async Task PostgreSqlDefaultNullUniquenessAllowsEachNullKey()
    {
        await MechanismDmlDatabase.ResetAsync(fixture.ConnectionString);
        await using var context = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);

        Assert.True(await context.InsertIgnoreAsync(new NullableUniqueRow { Key = null }, row => row.Key));
        Assert.True(await context.InsertIgnoreAsync(new NullableUniqueRow { Key = null }, row => row.Key));
        Assert.Equal(2, await context.NullableUnique.CountAsync());
    }

    [Fact]
    public async Task IgnoresOnlyTheSelectedUniqueTargetAndPreservesOtherSqlStates()
    {
        await MechanismDmlDatabase.ResetAsync(fixture.ConnectionString);
        await using var context = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        Assert.True(await context.InsertIgnoreAsync(
            new SecondaryUniqueRow { TargetKey = "target-1", SecondaryKey = "shared" },
            row => row.TargetKey));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.InsertIgnoreAsync(
            new SecondaryUniqueRow { TargetKey = "target-2", SecondaryKey = "shared" },
            row => row.TargetKey));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task RejectsPartialIndexShadowAndSplitMappingsBeforeOpeningConnection()
    {
        await MechanismDmlDatabase.EnsureSchemaScriptAppliedAsync(fixture.ConnectionString);
        await using var partial = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        var partialError = await Assert.ThrowsAsync<NotSupportedException>(() => partial.InsertIgnoreAsync(
            new PartialUniqueRow { Key = "partial", Enabled = true },
            row => row.Key));
        Assert.Contains("Partial unique", partialError.Message);
        Assert.Equal(ConnectionState.Closed, partial.Database.GetDbConnection().State);

        await using var shadow = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        await Assert.ThrowsAsync<NotSupportedException>(() => shadow.InsertIgnoreAsync(
            new ShadowValueRow { Key = "shadow" },
            row => row.Key));
        Assert.Equal(ConnectionState.Closed, shadow.Database.GetDbConnection().State);

        await using var split = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        await Assert.ThrowsAsync<NotSupportedException>(() => split.InsertIgnoreAsync(
            new SplitRow { Key = "split", Detail = "details" },
            row => row.Key));
        Assert.Equal(ConnectionState.Closed, split.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task UsesConfiguredSentinelForDefaultOmissionAndPreservesExplicitNull()
    {
        await MechanismDmlDatabase.ResetAsync(fixture.ConnectionString);
        await using var context = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        var omitted = new NullableDefaultRow { UniqueKey = "omitted" };
        var explicitNull = new NullableDefaultRow { UniqueKey = "explicit-null", Value = null };

        Assert.True(await context.InsertIgnoreAsync(omitted, row => row.UniqueKey));
        Assert.True(await context.InsertIgnoreAsync(explicitNull, row => row.UniqueKey));
        Assert.Equal(EntityState.Detached, context.Entry(omitted).State);
        Assert.Equal(EntityState.Detached, context.Entry(explicitNull).State);
        var values = await context.NullableDefaults.AsNoTracking().ToDictionaryAsync(row => row.UniqueKey, row => row.Value);
        Assert.Equal("database-default", values["omitted"]);
        Assert.Null(values["explicit-null"]);
    }

    [Fact]
    public async Task OmitsIdentityAlwaysSentinelButRejectsExplicitIdentityAndClientGeneration()
    {
        await MechanismDmlDatabase.ResetAsync(fixture.ConnectionString);
        await using var context = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        Assert.True(await context.InsertIgnoreAsync(new IdentityAlwaysRow { UniqueKey = "generated" }, row => row.UniqueKey));
        Assert.True(await context.IdentityAlways.AnyAsync(row => row.UniqueKey == "generated"));

        var identityConnection = context.Database.GetDbConnection();
        var explicitIdentity = await Assert.ThrowsAsync<NotSupportedException>(() => context.InsertIgnoreAsync(
            new IdentityAlwaysRow { Id = 7002, UniqueKey = "explicit" }, row => row.UniqueKey));
        Assert.Contains("identity-always", explicitIdentity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Closed, identityConnection.State);

        var clientGenerated = await Assert.ThrowsAsync<NotSupportedException>(() => context.InsertIgnoreAsync(
            new ClientGeneratedRow { UniqueKey = "client-generated" }, row => row.UniqueKey));
        Assert.Contains("Client-generated", clientGenerated.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, identityConnection.State);
    }

    [Fact]
    public async Task ConverterFailureOccursBeforeOpeningTheDatabaseConnection()
    {
        await MechanismDmlDatabase.EnsureSchemaScriptAppliedAsync(fixture.ConnectionString);
        await using var context = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.InsertIgnoreAsync(
            new ThrowingConvertedRow { Key = new ThrowingKey("fail") }, row => row.Key));

        Assert.Equal("forced value-converter failure", exception.Message);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task SupportsPrimaryAndAlternateUniqueTargetsAndRejectsArbitraryCompositeConstruction()
    {
        await MechanismDmlDatabase.ResetAsync(fixture.ConnectionString);
        await using var context = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        Assert.True(await context.InsertIgnoreAsync(
            new IdentityDefaultRow { Id = 7010, UniqueKey = "primary-target", Payload = "primary" },
            row => row.Id));
        Assert.False(await context.InsertIgnoreAsync(
            new IdentityDefaultRow { Id = 7010, UniqueKey = "primary-target-duplicate", Payload = "duplicate" },
            row => row.Id));
        Assert.True(await context.InsertIgnoreAsync(
            new SecondaryUniqueRow { TargetKey = "alternate-target", SecondaryKey = "secondary-1" },
            row => row.TargetKey));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => context.InsertIgnoreAsync(
            new CompositeUniqueRow { Left = "left", Right = "right", Payload = "arbitrary-constructor" },
            row => new CompositeTarget(row.Left, row.Right)));
        Assert.Contains("anonymous composite key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task RejectsOwnedSharedMappingBeforeOpeningConnection()
    {
        await MechanismDmlDatabase.EnsureSchemaScriptAppliedAsync(fixture.ConnectionString);
        await using var context = MechanismDmlDatabase.CreateContext(fixture.ConnectionString);
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => context.InsertIgnoreAsync(
            new OwnedMechanismDetails { Value = "owned" },
            row => row.Value));

        Assert.Contains("owned entity mappings", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task WrongProviderRejectsInsertIgnoreWithoutOpeningConnection()
    {
        await using var context = new DbContext(new DbContextOptionsBuilder<DbContext>()
            .UseProviderTestWarnings().UseSqlite("Data Source=:memory:").Options);
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => context.InsertIgnoreAsync(
            new IdentityDefaultRow { UniqueKey = "wrong-provider" }, row => row.UniqueKey));
        Assert.Contains("Npgsql EF Core provider", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
}

internal sealed record CompositeTarget(string Left, string Right);
