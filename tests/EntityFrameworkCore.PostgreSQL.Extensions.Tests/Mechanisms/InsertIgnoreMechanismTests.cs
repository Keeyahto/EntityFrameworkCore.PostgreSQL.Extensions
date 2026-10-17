using System.Data;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Mechanisms;

[Collection(PostgreSqlCollection.Name)]
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

        var rows = await context.IdentityDefaults.ToDictionaryAsync(row => row.UniqueKey);
        Assert.Equal(2, rows.Count);
        Assert.True(rows["generated"].Id > 0);
        Assert.NotEqual(default, rows["generated"].CreatedAt);
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
}
