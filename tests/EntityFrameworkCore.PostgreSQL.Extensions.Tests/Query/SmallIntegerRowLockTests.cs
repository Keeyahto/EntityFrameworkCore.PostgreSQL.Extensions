using System.Data.Common;
using System.Globalization;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class SmallIntegerRowLockTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task Promoted_smallint_predicate_locks_the_target_until_rollback()
        => await AssertShortKeyLockAsync(targetKey: 1, useShare: false, commitHolder: false);

    [Fact]
    public async Task Promoted_smallint_predicate_locks_the_target_until_commit()
        => await AssertShortKeyLockAsync(targetKey: 2, useShare: true, commitHolder: true);

    [Fact]
    public async Task Captured_and_compiled_short_and_int_parameters_preserve_their_values()
    {
        await ResetAsync();
        var observer = new SqlCommandObserver();
        await using var context = CreateContext(observer);
        await using var transaction = await context.Database.BeginTransactionAsync();

        Func<SmallIntegerLockContext, int, IAsyncEnumerable<SmallIntegerLockRow>> compiledInt = EF.CompileAsyncQuery(
            (SmallIntegerLockContext db, int key) => db.Rows.Where(row => row.ShortKey == key).ForUpdate());
        var intResults = new List<SmallIntegerLockRow>();
        await foreach (var row in compiledInt(context, 1))
        {
            intResults.Add(row);
        }

        Assert.Equal(new short[] { 1 }, intResults.Select(row => row.ShortKey));
        Assert.Contains("FOR UPDATE OF", Assert.Single(observer.Commands), StringComparison.Ordinal);
        observer.Clear();

        short capturedShortKey = 2;
        var capturedShortResult = await context.Rows
            .Where(row => row.ShortKey == capturedShortKey)
            .ForUpdate()
            .SingleAsync();
        Assert.Equal(capturedShortKey, capturedShortResult.ShortKey);
        Assert.Contains("FOR UPDATE OF", Assert.Single(observer.Commands), StringComparison.Ordinal);
        observer.Clear();

        Func<SmallIntegerLockContext, short, IAsyncEnumerable<SmallIntegerLockRow>> compiledShort = EF.CompileAsyncQuery(
            (SmallIntegerLockContext db, short key) => db.Rows.Where(row => key == row.ShortKey).ForShare());
        var shortResults = new List<SmallIntegerLockRow>();
        await foreach (var row in compiledShort(context, 2))
        {
            shortResults.Add(row);
        }

        Assert.Equal(new short[] { 2 }, shortResults.Select(row => row.ShortKey));
        Assert.Contains("FOR SHARE OF", Assert.Single(observer.Commands), StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Out_of_range_int_parameter_matches_native_overflow_without_locking_rows()
    {
        await ResetAsync();
        var outOfRangeKey = (int)short.MaxValue + 1;
        var nativeObserver = new SqlCommandObserver();
        var nativeExecutionObserver = new ReaderExecutionObserver();
        OverflowException nativeException;
        TransactionState nativeState;
        await using (var nativeContext = CreateContext(nativeObserver, nativeExecutionObserver))
        await using (var nativeTransaction = await nativeContext.Database.BeginTransactionAsync())
        {
            nativeException = await Assert.ThrowsAsync<OverflowException>(
                () => nativeContext.Rows.Where(row => row.ShortKey == outOfRangeKey).SingleOrDefaultAsync());
            Assert.Single(nativeObserver.Commands);
            Assert.Equal(0, nativeExecutionObserver.ReaderExecutedCount);
            nativeState = CaptureTransactionState(nativeContext, nativeTransaction);
        }

        var observer = new SqlCommandObserver();
        var executionObserver = new ReaderExecutionObserver();
        await using (var context = CreateContext(observer, executionObserver))
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var lockException = await Assert.ThrowsAsync<OverflowException>(
                () => context.Rows.Where(row => row.ShortKey == outOfRangeKey).ForUpdate().SingleOrDefaultAsync());
            Assert.Equal(nativeException.Message, lockException.Message);
            Assert.Contains("FOR UPDATE OF", Assert.Single(observer.Commands), StringComparison.Ordinal);
            Assert.Equal(0, executionObserver.ReaderExecutedCount);
            var lockState = CaptureTransactionState(context, transaction);
            Assert.Equal(nativeState, lockState);
            output.WriteLine(
                $"out-of-range parameter evidence: nativeOverflow={nativeException.GetType().Name};" +
                $"lockOverflow={lockException.GetType().Name};nativeState={nativeState};lockState={lockState};" +
                $"readerExecuted={executionObserver.ReaderExecutedCount};nowaitRows=1,2");
            Assert.Equal(new short[] { 1, 2 }, await ReadAllRowsWithNativeNowaitAsync());
        }
    }

    [Fact]
    public async Task Other_numeric_and_computed_predicates_reject_before_select()
    {
        await ResetAsync();
        var observer = new SqlCommandObserver();
        await using var context = CreateContext(observer);
        await using var transaction = await context.Database.BeginTransactionAsync();
        short shortValue = 1;

        var rejected = new Func<Task>[]
        {
            () => context.Rows.Where(row => (short)row.IntegerKey == (short)1).ForUpdate().ToListAsync(),
            () => context.Rows.Where(row => row.IntegerKey == (int)shortValue).ForUpdate().ToListAsync(),
            () => context.Rows.Where(row => row.ShortKey + 0 == 1).ForUpdate().ToListAsync(),
            () => context.Rows.Where(row => Convert.ToInt32(row.ShortKey, CultureInfo.InvariantCulture) == 1)
                .ForUpdate().ToListAsync(),
            () => context.Rows.Where(row => row.NullableShortKey == 1).ForUpdate().ToListAsync(),
            () => context.Rows.Where(row => (long)row.ShortKey == 1L).ForUpdate().ToListAsync()
        };

        foreach (var query in rejected)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(query);
            Assert.StartsWith("The PostgreSQL row-lock request cannot be fulfilled:", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, observer.Count);
        }

        await transaction.RollbackAsync();
    }

    private async Task AssertShortKeyLockAsync(short targetKey, bool useShare, bool commitHolder)
    {
        await ResetAsync();
        var observer = new SqlCommandObserver();
        await using var holder = CreateContext(observer);
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        var holderBackend = await BackendIdAsync(holder.Database.GetDbConnection(), holderTransaction.GetDbTransaction());
        var promotedKey = (int)targetKey;
        var selected = useShare
            ? await holder.Rows.Where(row => row.ShortKey == promotedKey).ForShare().SingleAsync()
            : await holder.Rows.Where(row => row.ShortKey == promotedKey).ForUpdate().SingleAsync();

        Assert.Equal(targetKey, selected.ShortKey);
        var selectSql = Assert.Single(observer.Commands);
        Assert.Contains(useShare ? "FOR SHARE OF" : "FOR UPDATE OF", selectSql, StringComparison.Ordinal);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var writerConnection = new NpgsqlConnection(fixture.ConnectionString);
        await writerConnection.OpenAsync(timeout.Token);
        await using var writerTransaction = await writerConnection.BeginTransactionAsync(timeout.Token);
        var writerBackend = await BackendIdAsync(writerConnection, writerTransaction, timeout.Token);
        var writerTask = UpdatePayloadAsync(
            writerConnection,
            writerTransaction,
            targetKey,
            $"target-writer-{targetKey}",
            timeout.Token);

        var writerTransactionCompleted = false;
        try
        {
            var wait = await WaitForBlockedUpdateAsync(
                writerBackend,
                holderBackend,
                timeout.Token);
            Assert.Equal("Lock", wait.WaitEventType);
            Assert.Contains(holderBackend, wait.BlockingPids);
            Assert.Contains("UPDATE \"smallint_lock\".\"ShortKeyRows\"", wait.Query, StringComparison.Ordinal);

            var nonTarget = targetKey == 1 ? (short)2 : (short)1;
            Assert.Equal(1, await UpdateInIndependentTransactionAsync(nonTarget, $"non-target-{nonTarget}"));
            Assert.False(writerTask.IsCompleted, "The target writer completed while the caller still held its row lock.");

            if (commitHolder)
            {
                await holderTransaction.CommitAsync(timeout.Token);
            }
            else
            {
                await holderTransaction.RollbackAsync(timeout.Token);
            }

            Assert.Equal(1, await writerTask.WaitAsync(TimeSpan.FromSeconds(5)));
            await writerTransaction.CommitAsync(timeout.Token);
            writerTransactionCompleted = true;
            Assert.Single(observer.Commands);
            output.WriteLine(
                $"short-key lock evidence: strength={(useShare ? "share" : "update")};target={targetKey};wait={wait.WaitEventType};" +
                $"blocker={holderBackend};nonTarget={nonTarget};release={(commitHolder ? "commit" : "rollback")};sql={selectSql}");
        }
        finally
        {
            if (!writerTask.IsCompleted)
            {
                timeout.Cancel();
                try
                {
                    await writerTask;
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                }
                catch (PostgresException) when (timeout.IsCancellationRequested)
                {
                }
            }

            if (holder.Database.CurrentTransaction is not null)
            {
                await holderTransaction.RollbackAsync(CancellationToken.None);
            }

            if (!writerTransactionCompleted && writerTransaction.Connection is not null)
            {
                await writerTransaction.RollbackAsync(CancellationToken.None);
            }
        }
    }

    private async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE SCHEMA IF NOT EXISTS "smallint_lock";
            DROP TABLE IF EXISTS "smallint_lock"."ShortKeyRows";
            CREATE TABLE "smallint_lock"."ShortKeyRows" (
                "ShortKey" smallint PRIMARY KEY,
                "IntegerKey" integer NOT NULL,
                "NullableShortKey" smallint NULL,
                "Payload" text NOT NULL);
            INSERT INTO "smallint_lock"."ShortKeyRows" ("ShortKey", "IntegerKey", "NullableShortKey", "Payload")
            VALUES (1, 101, 1, 'first'), (2, 102, 2, 'second');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private SmallIntegerLockContext CreateContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<SmallIntegerLockContext>()
            .UseProviderTestWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions());
        builder.AddInterceptors(interceptors);
        return new SmallIntegerLockContext(builder.Options);
    }

    private static TransactionState CaptureTransactionState(
        SmallIntegerLockContext context,
        IDbContextTransaction transaction)
    {
        var currentTransactionPresent = context.Database.CurrentTransaction is not null;
        var connectionState = context.Database.GetDbConnection().State;
        try
        {
            return new(
                currentTransactionPresent,
                StoreTransactionDisposed: false,
                StoreConnectionPresent: transaction.GetDbTransaction().Connection is not null,
                ConnectionState: connectionState);
        }
        catch (ObjectDisposedException)
        {
            return new(currentTransactionPresent, StoreTransactionDisposed: true, StoreConnectionPresent: false, connectionState);
        }
    }

    private sealed class ReaderExecutionObserver : DbCommandInterceptor
    {
        private int _readerExecutedCount;

        public int ReaderExecutedCount => Volatile.Read(ref _readerExecutedCount);

        public override DbDataReader ReaderExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result)
        {
            Interlocked.Increment(ref _readerExecutedCount);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readerExecutedCount);
            return ValueTask.FromResult(result);
        }
    }

    private async Task<int> UpdateInIndependentTransactionAsync(short key, string payload)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var affected = await UpdatePayloadAsync(connection, transaction, key, payload, CancellationToken.None);
        await transaction.CommitAsync();
        return affected;
    }

    private async Task<short[]> ReadAllRowsWithNativeNowaitAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT \"ShortKey\" FROM \"smallint_lock\".\"ShortKeyRows\" ORDER BY \"ShortKey\" FOR UPDATE NOWAIT";
        await using var reader = await command.ExecuteReaderAsync();
        var keys = new List<short>();
        while (await reader.ReadAsync())
        {
            keys.Add(reader.GetInt16(0));
        }

        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return keys.ToArray();
    }

    private async Task<ObservedLockWait> WaitForBlockedUpdateAsync(
        int writerBackend,
        int holderBackend,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT wait_event_type, query, pg_catalog.pg_blocking_pids(pid)
                FROM pg_catalog.pg_stat_activity
                WHERE pid = @pid AND state = 'active'
                """;
            command.Parameters.AddWithValue("pid", writerBackend);
            ObservedLockWait? observation = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    var waitType = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var query = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var blockers = reader.GetFieldValue<int[]>(2);
                    if (waitType == "Lock"
                        && blockers.Contains(holderBackend)
                        && query.Contains("ShortKeyRows", StringComparison.Ordinal)
                        && query.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
                    {
                        observation = new ObservedLockWait(waitType, query, blockers);
                    }
                }
            }

            if (observation is not null)
            {
                return observation;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException($"Writer backend {writerBackend} did not wait on the smallint row lock held by backend {holderBackend}.");
    }

    private static async Task<int> UpdatePayloadAsync(
        DbConnection connection,
        DbTransaction transaction,
        short key,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE \"smallint_lock\".\"ShortKeyRows\" SET \"Payload\" = @payload WHERE \"ShortKey\" = @key";
        command.Parameters.Add(new NpgsqlParameter("payload", payload));
        command.Parameters.Add(new NpgsqlParameter("key", key));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> BackendIdAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_catalog.pg_backend_pid()";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private sealed record ObservedLockWait(string? WaitEventType, string Query, int[] BlockingPids);

    private sealed record TransactionState(
        bool CurrentTransactionPresent,
        bool StoreTransactionDisposed,
        bool StoreConnectionPresent,
        System.Data.ConnectionState ConnectionState);
}

internal sealed class SmallIntegerLockContext(DbContextOptions<SmallIntegerLockContext> options) : DbContext(options)
{
    public DbSet<SmallIntegerLockRow> Rows => Set<SmallIntegerLockRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SmallIntegerLockRow>(entity =>
        {
            entity.ToTable("ShortKeyRows", "smallint_lock");
            entity.HasKey(row => row.ShortKey);
            entity.Property(row => row.ShortKey).HasColumnType("smallint");
            entity.Property(row => row.IntegerKey).HasColumnType("integer");
            entity.Property(row => row.NullableShortKey).HasColumnType("smallint");
        });
    }
}

internal sealed class SmallIntegerLockRow
{
    public short ShortKey { get; set; }
    public int IntegerKey { get; set; }
    public short? NullableShortKey { get; set; }
    public string Payload { get; set; } = string.Empty;
}
