using System.Data;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Mechanisms;

[Collection(PostgreSqlCollection.Name)]
public sealed class CommandMechanismTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task TextLockBlocksSameKeyAndReleasesAfterCommit()
    {
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        await using var observer = fixture.CreateContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        var key = $"text-lock-{Guid.NewGuid():N}";

        await first.Database.AcquireTransactionAdvisoryLockAsync(key);
        var secondPid = await GetBackendPidAsync(second);
        var waiting = second.Database.AcquireTransactionAdvisoryLockAsync(key);
        await WaitForLockWaitAsync(observer, secondPid, TimeSpan.FromSeconds(10));
        Assert.False(waiting.IsCompleted);

        await firstTransaction.CommitAsync();
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await secondTransaction.CommitAsync();
    }

    [Fact]
    public async Task TextLockMatchesPostgreSqlHashExtendedWithSeedZero()
    {
        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await using var nativeConnection = new NpgsqlConnection(fixture.ConnectionString);
        await nativeConnection.OpenAsync();
        await using var nativeTransaction = await nativeConnection.BeginTransactionAsync();
        var key = $"native-text-lock-{Guid.NewGuid():N}";

        await context.Database.AcquireTransactionAdvisoryLockAsync(key);

        Assert.False(await TryNativeTextXactLockAsync(nativeConnection, nativeTransaction, key));
        Assert.True(await TryNativeTextXactLockAsync(nativeConnection, nativeTransaction, key + ":different"));

        await transaction.CommitAsync();
        Assert.True(await TryNativeTextXactLockAsync(nativeConnection, nativeTransaction, key));
        await nativeTransaction.CommitAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IntegerLockReleasesAfterCommitOrRollback(bool commit)
    {
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        await using var observer = fixture.CreateContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        var firstKey = Random.Shared.Next(1_000_000, int.MaxValue);

        await first.Database.AcquireTransactionAdvisoryLockAsync((long)firstKey);
        var secondPid = await GetBackendPidAsync(second);
        var waiting = second.Database.AcquireTransactionAdvisoryLockAsync((long)firstKey);
        await WaitForLockWaitAsync(observer, secondPid, TimeSpan.FromSeconds(10));

        if (commit)
        {
            await firstTransaction.CommitAsync();
        }
        else
        {
            await firstTransaction.RollbackAsync();
        }

        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await secondTransaction.CommitAsync();
        await using var independent = fixture.CreateContext();
        await using var independentTransaction = await independent.Database.BeginTransactionAsync();
        await independent.Database.AcquireTransactionAdvisoryLockAsync(23, 41);
        await independentTransaction.RollbackAsync();
    }

    [Fact]
    public async Task DifferentKeysAndDifferentIntegerKeySpacesRemainIndependent()
    {
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        await using var secondTransaction = await second.Database.BeginTransactionAsync();

        await first.Database.AcquireTransactionAdvisoryLockAsync(73_105_911L);
        await second.Database.AcquireTransactionAdvisoryLockAsync(0, 73_105_911);
        await first.Database.AcquireTransactionAdvisoryLockAsync("independent:first:" + Guid.NewGuid().ToString("N"));
        await second.Database.AcquireTransactionAdvisoryLockAsync("independent:second:" + Guid.NewGuid().ToString("N"));

        await secondTransaction.CommitAsync();
        await firstTransaction.RollbackAsync();
    }

    [Fact]
    public async Task BorrowedNpgsqlTransactionOwnsTheAdvisoryLockLifetime()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var borrowed = await connection.BeginTransactionAsync();
        var options = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings().UseNpgsql(connection).Options;
        await using var context = new ProbeContext(options);
        await context.Database.UseTransactionAsync(borrowed);
        var key = 5_400_000_000L + Random.Shared.Next(1, int.MaxValue);

        await context.Database.AcquireTransactionAdvisoryLockAsync(key);
        await using var competitor = fixture.CreateContext();
        await using var competitorTransaction = await competitor.Database.BeginTransactionAsync();
        var competitorPid = await GetBackendPidAsync(competitor);
        var blocked = competitor.Database.AcquireTransactionAdvisoryLockAsync(key);
        await using var observer = fixture.CreateContext();
        await WaitForLockWaitAsync(observer, competitorPid, TimeSpan.FromSeconds(10));

        await borrowed.CommitAsync();
        await blocked.WaitAsync(TimeSpan.FromSeconds(10));
        await competitorTransaction.CommitAsync();
    }

    [Fact]
    public async Task MissingTransactionIsRejectedBeforeOpeningConnection()
    {
        await using var context = fixture.CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.AcquireTransactionAdvisoryLockAsync("missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.SetTransactionReadOnlyAsync());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task CancellationWhileWaitingIsPropagatedWithoutFallback()
    {
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        var key = $"cancel-lock-{Guid.NewGuid():N}";
        await first.Database.AcquireTransactionAdvisoryLockAsync(key);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var failure = await Record.ExceptionAsync(() => second.Database.AcquireTransactionAdvisoryLockAsync(key, cancellation.Token));

        Assert.True(failure is OperationCanceledException or PostgresException { SqlState: PostgresErrorCodes.QueryCanceled },
            $"Expected cancellation to propagate, observed {failure?.GetType().Name ?? "successful completion"}.");
        await firstTransaction.RollbackAsync();
        await secondTransaction.RollbackAsync();
    }

    [Fact]
    public async Task ReadOnlySettingRejectsWritesAndRollbackLeavesLaterTransactionWritable()
    {
        await using var context = fixture.CreateContext();
        await using (var readOnly = await context.Database.BeginTransactionAsync())
        {
            await context.Database.SetTransactionReadOnlyAsync();
            var failure = await Assert.ThrowsAsync<PostgresException>(() => InsertProbeRowAsync(context, 71001));
            Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, failure.SqlState);
            await readOnly.RollbackAsync();
        }

        await using var writable = await context.Database.BeginTransactionAsync();
        await InsertProbeRowAsync(context, 71002);
        await writable.CommitAsync();
    }

    [Fact]
    public async Task LateReadOnlyCallKeepsPostgreSqlObservedTransactionBehavior()
    {
        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        _ = await context.Database.SqlQuery<int>($"SELECT 1 AS \"Value\"").SingleAsync();

        await context.Database.SetTransactionReadOnlyAsync();
        var failure = await Assert.ThrowsAsync<PostgresException>(() => InsertProbeRowAsync(context, 71003));
        Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, failure.SqlState);
        await transaction.RollbackAsync();
    }

    private static async Task<bool> TryNativeTextXactLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_try_advisory_xact_lock(hashtextextended(@text, 0))";
        command.Parameters.Add(new NpgsqlParameter("text", key));
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertProbeRowAsync(ProbeContext context, int id)
    {
        const string payload = "command-test";
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"Rows\" (\"Id\", \"PeerId\", \"Rank\", \"Payload\") VALUES ({id}, 0, 0, {payload})");
    }

    private static async Task<int> GetBackendPidAsync(ProbeContext context)
        => await context.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    private static async Task WaitForLockWaitAsync(ProbeContext observer, int backendPid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var waitType = await observer.Database.SqlQuery<string?>(
                $"SELECT wait_event_type AS \"Value\" FROM pg_catalog.pg_stat_activity WHERE pid = {backendPid}").SingleOrDefaultAsync();
            if (string.Equals(waitType, "Lock", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException($"PostgreSQL backend {backendPid} did not enter a lock wait within {timeout}.");
    }
}
