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
public sealed class QueryFailureTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Native_ef_nowait_error_wrapper_matches_unmarked_npgsql_control(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "native-error-control" });

        await using var holder = fixture.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.Rows.FromSqlRaw("SELECT * FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync();

        var observer = new SqlCommandObserver();
        await using var control = QueryTestSupport.CreateContext(fixture, null, observer);
        await using var controlTransaction = await control.Database.BeginTransactionAsync();
        var wrapper = await Assert.ThrowsAsync<InvalidOperationException>(() => control.Rows
            .FromSqlRaw("SELECT * FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE NOWAIT")
            .ToListAsync());
        var native = QueryTestSupport.AssertNativePostgresFailure(wrapper, PostgresErrorCodes.LockNotAvailable);
        Assert.Single(observer.Commands);

        await controlTransaction.RollbackAsync();
        await holderTransaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "P3-native-wrapper-control", candidate, "supported-pass",
            $"nativeSqlState={native.SqlState};providerWrapper={wrapper.GetType().Name};executed={OneLine(observer.Commands.Single())};commandCount={observer.Count}");
    }

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Generated_locking_query_preserves_serialization_failure_40001(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "before" });

        var observer = new SqlCommandObserver();
        await using var snapshot = QueryTestSupport.CreateContext(fixture, candidate, observer);
        await using var snapshotTransaction = await snapshot.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var oldValue = await snapshot.Rows.Where(row => row.Id == 1).Select(row => row.Payload).SingleAsync();
        Assert.Equal("before", oldValue);
        var snapshotBackend = await BackendIdAsync(snapshot.Database.GetDbConnection(), snapshotTransaction.GetDbTransaction());

        await using var updater = fixture.CreateContext();
        await using var updaterTransaction = await updater.Database.BeginTransactionAsync();
        var row = await updater.Rows.SingleAsync(item => item.Id == 1);
        row.Payload = "after";
        await updater.SaveChangesAsync();
        var updaterBackend = await BackendIdAsync(updater.Database.GetDbConnection(), updaterTransaction.GetDbTransaction());
        await updaterTransaction.CommitAsync();
        Assert.NotEqual(snapshotBackend, updaterBackend);

        var query = snapshot.Rows.Where(item => item.Id == 1).ForUpdate();
        var generatedSql = query.ToQueryString();
        var wrapper = await Assert.ThrowsAsync<InvalidOperationException>(() => query.SingleAsync());
        var native = QueryTestSupport.AssertNativePostgresFailure(wrapper, PostgresErrorCodes.SerializationFailure);
        Assert.Equal(2, observer.Count);
        Assert.Contains("FOR UPDATE OF", observer.Commands.Last(), StringComparison.Ordinal);

        await snapshotTransaction.RollbackAsync(CancellationToken.None);
        QueryTestSupport.WriteOutcome(output, "P3-serialization-40001", candidate, "native-failure-preserved",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands.Last())};backendIds={snapshotBackend},{updaterBackend};snapshotValue={oldValue};nativeSqlState={native.SqlState};providerWrapper={wrapper.GetType().Name};commands={observer.Count};rollback=after-abort");
    }

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Generated_locking_query_deadlock_preserves_40P01_with_observed_lock_waits(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "left" },
            new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "right" });

        var leftObserver = new SqlCommandObserver();
        var rightObserver = new SqlCommandObserver();
        await using var left = QueryTestSupport.CreateContext(fixture, candidate, leftObserver);
        await using var leftTransaction = await left.Database.BeginTransactionAsync();
        await using var right = QueryTestSupport.CreateContext(fixture, candidate, rightObserver);
        await using var rightTransaction = await right.Database.BeginTransactionAsync();
        await left.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync();
        await right.Rows.Where(row => row.Id == 2).ForUpdate().SingleAsync();
        var leftBackend = await BackendIdAsync(left.Database.GetDbConnection(), leftTransaction.GetDbTransaction());
        var rightBackend = await BackendIdAsync(right.Database.GetDbConnection(), rightTransaction.GetDbTransaction());
        Assert.NotEqual(leftBackend, rightBackend);

        var leftQuery = left.Rows.Where(row => row.Id == 2).ForUpdate();
        var rightQuery = right.Rows.Where(row => row.Id == 1).ForUpdate();
        var leftSql = leftQuery.ToQueryString();
        var rightSql = rightQuery.ToQueryString();
        var leftTask = leftQuery.SingleAsync();
        var leftWait = WaitForLockWaitStateAsync(fixture, leftBackend, TimeSpan.FromSeconds(5));
        var leftWaitState = await leftWait;
        Assert.Equal("Lock", leftWaitState);
        var rightWait = WaitForLockWaitStateAsync(fixture, rightBackend, TimeSpan.FromSeconds(5));
        var rightTask = rightQuery.SingleAsync();
        var rightWaitState = await rightWait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Lock", rightWaitState);

        var leftFailureTask = CaptureExpectedWrapperAsync(leftTask);
        var rightFailureTask = CaptureExpectedWrapperAsync(rightTask);
        var failures = await Task.WhenAll(leftFailureTask, rightFailureTask).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True((failures[0] is null) != (failures[1] is null), "Exactly one lock waiter must become the PostgreSQL deadlock victim.");
        var deadlockWrapper = failures.SingleOrDefault(failure => failure is not null)!;
        var native = QueryTestSupport.AssertNativePostgresFailure(deadlockWrapper, PostgresErrorCodes.DeadlockDetected);
        var survivorId = failures[0] is null ? 2 : 1;
        Assert.Contains("FOR UPDATE OF", leftObserver.Commands.Last(), StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF", rightObserver.Commands.Last(), StringComparison.Ordinal);

        await leftTransaction.RollbackAsync(CancellationToken.None);
        await rightTransaction.RollbackAsync(CancellationToken.None);
        QueryTestSupport.WriteOutcome(output, "P3-deadlock-40P01", candidate, "native-failure-preserved",
            $"generated={OneLine(leftSql)}|{OneLine(rightSql)};executed={OneLine(leftObserver.Commands.Last())}|{OneLine(rightObserver.Commands.Last())};backendIds={leftBackend},{rightBackend};waitEventTypes={leftWaitState},{rightWaitState};nativeSqlState={native.SqlState};victim={deadlockWrapper.GetType().Name};survivorId={survivorId};commands={leftObserver.Count},{rightObserver.Count};rollback=after-deadlock");
    }

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Terminated_waiting_backend_preserves_postgresql_admin_shutdown(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "terminated" });

        await using var holder = fixture.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.Rows.FromSqlRaw("SELECT * FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync();

        var observer = new SqlCommandObserver();
        await using var target = QueryTestSupport.CreateContext(fixture, candidate, observer);
        var targetTransaction = await target.Database.BeginTransactionAsync();
        var targetBackend = await BackendIdAsync(target.Database.GetDbConnection(), targetTransaction.GetDbTransaction());
        var query = target.Rows.Where(row => row.Id == 1).ForUpdate();
        var generatedSql = query.ToQueryString();
        var queryTask = query.SingleAsync();
        var waitEvent = await WaitForLockWaitStateAsync(fixture, targetBackend, TimeSpan.FromSeconds(5));

        await using var administrator = new NpgsqlConnection(fixture.ConnectionString);
        await administrator.OpenAsync();
        await using (var command = administrator.CreateCommand())
        {
            command.CommandText = "SELECT pg_terminate_backend(@pid)";
            command.Parameters.AddWithValue("pid", targetBackend);
            Assert.Equal(true, await command.ExecuteScalarAsync());
        }

        var wrapper = await Assert.ThrowsAsync<InvalidOperationException>(() => queryTask);
        var native = QueryTestSupport.AssertNativePostgresFailure(wrapper, PostgresErrorCodes.AdminShutdown);
        Assert.Single(observer.Commands);
        await target.Database.CloseConnectionAsync();
        await targetTransaction.DisposeAsync();
        await holderTransaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "P3-backend-termination", candidate, "native-failure-preserved",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands.Single())};backendId={targetBackend};waitEventType={waitEvent};nativeSqlState={native.SqlState};providerWrapper={wrapper.GetType().Name};commands={observer.Count};targetCleanup=terminated-connection-closed;holderRollback=release");
    }

    private static async Task<int> BackendIdAsync(DbConnection connection, DbTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_backend_pid()";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string> WaitForLockWaitStateAsync(
        PostgreSqlFixture fixture,
        int backendId,
        TimeSpan timeout)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        using var deadline = new CancellationTokenSource(timeout);
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
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"PostgreSQL backend {backendId} did not enter a lock wait within {timeout}.");
    }

    private static async Task<InvalidOperationException?> CaptureExpectedWrapperAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
