using System.Data;
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
public sealed class SortKeyConcurrencyTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    private const string NativeOrderedLockSql = "SELECT \"Id\", \"PeerId\", \"Rank\", \"Payload\" FROM \"Rows\" ORDER BY \"Rank\" LIMIT 1 FOR UPDATE";

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task P36_read_committed_order_by_take_wait_returns_originally_selected_row(PocCarrier candidate)
    {
        await RunScenarioAsync(candidate, useNativeControl: true);
        await RunScenarioAsync(candidate, useNativeControl: false);
    }

    private async Task RunScenarioAsync(PocCarrier candidate, bool useNativeControl)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "original-sort-first" },
            new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "other-row" });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = deadline.Token;
        var observer = new SqlCommandObserver();
        await using var holder = fixture.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockRowAsync(holder.Database.GetDbConnection(), holderTransaction.GetDbTransaction(), 1, cancellationToken);
        var holderBackend = await BackendIdAsync(holder.Database.GetDbConnection(), holderTransaction.GetDbTransaction(), cancellationToken);

        await using var waiter = QueryTestSupport.CreateContext(fixture, useNativeControl ? null : candidate, observer);
        await using var waiterTransaction = await waiter.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var waiterBackend = await BackendIdAsync(waiter.Database.GetDbConnection(), waiterTransaction.GetDbTransaction(), cancellationToken);
        Assert.NotEqual(holderBackend, waiterBackend);

        var query = waiter.Rows.OrderBy(row => row.Rank).Take(1).ForUpdate();
        var generatedSql = useNativeControl ? NativeOrderedLockSql : query.ToQueryString();
        Task<ProbeRow>? waitingQuery = null;
        var holderCommitted = false;
        var waiterRolledBack = false;
        string? waitEvent = null;

        try
        {
            waitingQuery = useNativeControl
                ? ReadProbeRowAsync(waiter.Database.GetDbConnection(), waiterTransaction.GetDbTransaction(), NativeOrderedLockSql, cancellationToken)
                : ReadGeneratedRowAsync(query, cancellationToken);

            waitEvent = await WaitForLockWaitStateAsync(fixture, waiterBackend, TimeSpan.FromSeconds(8), cancellationToken);
            Assert.Equal("Lock", waitEvent);

            await using (var update = holder.Database.GetDbConnection().CreateCommand())
            {
                update.Transaction = holderTransaction.GetDbTransaction();
                update.CommandText = "UPDATE \"Rows\" SET \"Rank\" = 30 WHERE \"Id\" = 1";
                Assert.Equal(1, await update.ExecuteNonQueryAsync(cancellationToken));
            }

            await holderTransaction.CommitAsync(cancellationToken);
            holderCommitted = true;
            var selected = await waitingQuery.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            Assert.Equal(1, selected.Id);
            Assert.Equal(30, selected.Rank);
            if (useNativeControl)
            {
                Assert.Empty(observer.Commands);
            }
            else
            {
                Assert.Single(observer.Commands);
            }

            await using var competitor = fixture.CreateContext();
            await using var competitorTransaction = await competitor.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var competitorBackend = await BackendIdAsync(competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(), cancellationToken);
            Assert.NotEqual(waiterBackend, competitorBackend);
            await ExecuteAsync(competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
                "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 2 FOR UPDATE NOWAIT", cancellationToken);
            var targetConflict = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
                "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE NOWAIT", cancellationToken));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, targetConflict.SqlState);
            await competitorTransaction.RollbackAsync(cancellationToken);
            await waiterTransaction.RollbackAsync(cancellationToken);
            waiterRolledBack = true;

            if (useNativeControl)
            {
                Assert.Equal(NativeOrderedLockSql, OneLine(generatedSql));
                QueryTestSupport.WriteOutcome(output, "P3.6-sort-key-native", candidate, "native-control-pass",
                    $"sql={OneLine(generatedSql)};executed={OneLine(NativeOrderedLockSql)};backendIds={holderBackend},{waiterBackend},{competitorBackend};waitEvent={waitEvent};returnedId={selected.Id};returnedRank={selected.Rank};nonTargetNowait=pass;targetNowait={targetConflict.SqlState};waiterRelease=rollback;orderingAfterWait=not-asserted-beyond-native-observed-row");
            }
            else
            {
                var executedSql = Assert.Single(observer.Commands);
                Assert.Contains("ORDER BY", executedSql, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("LIMIT", executedSql, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("FOR UPDATE OF", executedSql, StringComparison.Ordinal);
                QueryTestSupport.WriteOutcome(output, "P3.6-sort-key-generated", candidate, "supported-pass",
                    $"generated={OneLine(generatedSql)};executed={OneLine(executedSql)};backendIds={holderBackend},{waiterBackend},{competitorBackend};waitEvent={waitEvent};returnedId={selected.Id};returnedRank={selected.Rank};nonTargetNowait=pass;targetNowait={targetConflict.SqlState};waiterRelease=rollback;orderingAfterWait=not-asserted-beyond-native-observed-row");
            }

        }
        finally
        {
            if (!holderCommitted)
            {
                await holderTransaction.RollbackAsync(CancellationToken.None);
            }

            deadline.Cancel();
            if (waitingQuery is not null)
            {
                await waitingQuery.WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (!waiterRolledBack)
            {
                await waiterTransaction.RollbackAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<ProbeRow> ReadGeneratedRowAsync(IQueryable<ProbeRow> query, CancellationToken cancellationToken)
    {
        var row = await query.SingleAsync(cancellationToken);
        return row;
    }

    private static async Task<ProbeRow> ReadProbeRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The native ordered locking query returned no row.");
        }

        return new ProbeRow
        {
            Id = reader.GetInt32(0),
            PeerId = reader.GetInt32(1),
            Rank = reader.GetInt32(2),
            Payload = reader.GetString(3)
        };
    }

    private static async Task LockRowAsync(DbConnection connection, DbTransaction transaction, int rowId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM \"Rows\" WHERE \"Id\" = @id FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = rowId;
        command.Parameters.Add(parameter);
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<int> BackendIdAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_backend_pid()";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<object?> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<string> WaitForLockWaitStateAsync(
        PostgreSqlFixture fixture,
        int backendId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (!deadline.IsCancellationRequested)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT wait_event_type FROM pg_stat_activity WHERE pid = @pid";
            command.Parameters.AddWithValue("pid", backendId);
            var result = await command.ExecuteScalarAsync(deadline.Token);
            if (result is string waitEvent && waitEvent == "Lock")
            {
                return waitEvent;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"PostgreSQL backend {backendId} did not enter a lock wait within {timeout}.");
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
