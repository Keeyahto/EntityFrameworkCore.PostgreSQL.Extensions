using System.Data.Common;
using EntityFrameworkCore.PostgreSQL.Extensions;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class LockModeRuntimeTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{

[Theory]
[InlineData(LockStrength.NoKeyUpdate)]
[InlineData(LockStrength.KeyShare)]
public async Task Additional_strengths_lock_the_target_and_preserve_native_sqlstate(
        LockStrength strength)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "target" });

        var observer = new SqlCommandObserver();
        await using var holder = QueryTestSupport.CreateContext(fixture, observer);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        var selected = await holder.Rows.Where(row => row.Id == 1).WithRowLock(strength, LockWait.Wait).SingleAsync();
        Assert.Equal(1, selected.Id);
        Assert.Contains(LockClause(strength), observer.Commands.Single(), StringComparison.Ordinal);
        Assert.Contains($"OF {QueryTestSupport.RootAlias(observer.Commands.Single())}", observer.Commands.Single(), StringComparison.Ordinal);

        await using var competitor = fixture.CreateContext();
        await using var otherTransaction = await competitor.Database.BeginTransactionAsync();
        var conflict = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            competitor.Database.GetDbConnection(),
            otherTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE NOWAIT"));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, conflict.SqlState);
        await otherTransaction.RollbackAsync();
        await transaction.RollbackAsync();

        QueryTestSupport.WriteOutcome(output, $"Q03/Q-mode-{strength}", "supported-pass",
            $"executed={OneLine(observer.Commands.Single())};selectedIds={selected.Id};conflictSqlState={conflict.SqlState};release=rollback");
    }

    [Fact]
    public async Task No_wait_surfaces_postgresql_55P03_without_retry()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "target" });

        await using var holder = fixture.CreateContext(enableExtensions: true);
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        _ = await holder.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync();

        var observer = new SqlCommandObserver();
        await using var contender = QueryTestSupport.CreateContext(fixture, observer);
        await using var contenderTransaction = await contender.Database.BeginTransactionAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => contender.Rows
            .Where(row => row.Id == 1)
            .ForUpdate(LockWait.NoWait)
            .SingleAsync());
        var nativeFailure = QueryTestSupport.AssertNativePostgresFailure(failure, PostgresErrorCodes.LockNotAvailable);
        Assert.Contains("FOR UPDATE OF", observer.Commands.Single(), StringComparison.Ordinal);
        Assert.Contains("NOWAIT", observer.Commands.Single(), StringComparison.Ordinal);
        await contenderTransaction.RollbackAsync();
        await holderTransaction.CommitAsync();

        QueryTestSupport.WriteOutcome(output, "Q01-NOWAIT", "supported-pass",
            $"executed={OneLine(observer.Commands.Single())};providerWrapper={failure.GetType().Name};sqlState={nativeFailure.SqlState};retryCount=0;release=commit");
    }

    [Fact]
    public async Task Skip_locked_returns_only_unlocked_rows()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "held" },
            new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "available" });

        await using var holder = fixture.CreateContext(enableExtensions: true);
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        _ = await holder.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync();

        var observer = new SqlCommandObserver();
        await using var worker = QueryTestSupport.CreateContext(fixture, observer);
        await using var workerTransaction = await worker.Database.BeginTransactionAsync();
        var selected = await worker.Rows.ForUpdate(LockWait.SkipLocked).ToListAsync();

        Assert.Equal(new[] { 2 }, selected.Select(row => row.Id).OrderBy(id => id));
        Assert.Contains("SKIP LOCKED", observer.Commands.Single(), StringComparison.Ordinal);
        await workerTransaction.CommitAsync();
        await holderTransaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "Q04-SKIP-LOCKED", "supported-pass",
            $"executed={OneLine(observer.Commands.Single())};selectedIds={string.Join(',', selected.Select(row => row.Id))};holderRollback=release");
    }

    [Fact]
    public async Task Waiting_lock_cancellation_is_observed_in_postgresql_and_propagated()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "held" });

        await using var holder = fixture.CreateContext(enableExtensions: true);
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        _ = await holder.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync();

        var observer = new SqlCommandObserver();
        await using var waiting = QueryTestSupport.CreateContext(fixture, observer);
        var waitingTransaction = await waiting.Database.BeginTransactionAsync();
        var waitingBackendId = await BackendIdAsync(waiting.Database.GetDbConnection(), waitingTransaction.GetDbTransaction());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var task = waiting.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync(cancellation.Token);
        string? waitState = null;
        Exception? waitObservationFailure = null;
        try
        {
            waitState = await WaitForLockWaitStateAsync(fixture, waitingBackendId, cancellation.Token);
        }
        catch (Exception exception)
        {
            waitObservationFailure = exception;
        }

        cancellation.Cancel();
        Exception? commandFailure = null;
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            commandFailure = exception;
        }
        await waitingTransaction.RollbackAsync(CancellationToken.None);
        await holderTransaction.RollbackAsync();
        Assert.Null(waitObservationFailure);
        Assert.Equal("Lock", waitState);
        Assert.True(commandFailure is OperationCanceledException
            || commandFailure is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled },
            $"Expected a propagated cancellation, got {commandFailure?.GetType().Name}: {commandFailure?.Message}");
        Assert.Single(observer.Commands);
        QueryTestSupport.WriteOutcome(output, "Q01-CANCEL-WAIT", "supported-pass",
            $"executed={OneLine(observer.Commands.Single())};waitingBackendId={waitingBackendId};waitEventType={waitState};cancelled={commandFailure!.GetType().Name};rollback=release");
    }

    [Fact]
    public async Task Query_built_before_transaction_fails_at_execution_without_issuing_select()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "no-tx" });

        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
        var query = context.Rows.Where(row => row.Id == 1).ForUpdate();
        var generatedSql = query.ToQueryString();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => query.SingleAsync());
        Assert.StartsWith("The PostgreSQL row-lock request cannot be fulfilled:", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, observer.Count);
        QueryTestSupport.WriteOutcome(output, "Q17-missing-transaction", "explicit-reject-as-designed",
            $"generated={OneLine(generatedSql)};reason={failure.Message};issuedCommands={observer.Count}");
    }

    [Fact]
    public async Task Borrowed_ef_transaction_is_accepted_and_owns_the_lock()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "borrowed" });

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var nativeTransaction = await connection.BeginTransactionAsync();
        var observer = new SqlCommandObserver();
        var options = new DbContextOptionsBuilder<ProbeContext>().UseProviderTestWarnings()
            .UseNpgsql(connection, npgsql => npgsql.UsePostgreSqlExtensions())
            .AddInterceptors(observer)
            .Options;
        await using var context = new ProbeContext(options);
        await context.Database.UseTransactionAsync(nativeTransaction);
        var selected = await context.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync();

        await using var competitor = fixture.CreateContext();
        await using var competitorTransaction = await competitor.Database.BeginTransactionAsync();
        var conflict = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE NOWAIT"));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, conflict.SqlState);
        Assert.Single(observer.Commands);
        await competitorTransaction.RollbackAsync();
        await nativeTransaction.CommitAsync();

        QueryTestSupport.WriteOutcome(output, "Q17-borrowed-transaction", "supported-pass",
            $"executed={OneLine(observer.Commands.Single())};selectedIds={selected.Id};targetNowait={conflict.SqlState};release=borrowed-commit");
    }

    private static string LockClause(LockStrength strength) => strength switch
    {
        LockStrength.NoKeyUpdate => "FOR NO KEY UPDATE",
        LockStrength.KeyShare => "FOR KEY SHARE",
        _ => throw new ArgumentOutOfRangeException(nameof(strength))
    };

    private static async Task<object?> ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<int> BackendIdAsync(DbConnection connection, DbTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_backend_pid()";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> WaitForLockWaitStateAsync(
        PostgreSqlFixture fixture,
        int backendId,
        CancellationToken cancellationToken)
    {
        await using var observerConnection = new NpgsqlConnection(fixture.ConnectionString);
        await observerConnection.OpenAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!deadline.IsCancellationRequested)
        {
            await using var command = observerConnection.CreateCommand();
            command.CommandText = "SELECT wait_event_type FROM pg_stat_activity WHERE pid = @pid";
            command.Parameters.AddWithValue("pid", backendId);
            object? result;
            try
            {
                result = await command.ExecuteScalarAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (result is string waitEvent && waitEvent == "Lock")
            {
                return waitEvent;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"Backend {backendId} did not enter a PostgreSQL lock wait before the deadline.");
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
