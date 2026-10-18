using System.Collections.Concurrent;
using System.Data;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Mechanisms;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
[Trait("MechanismTestCategory", "PostgreSQL")]
public sealed class DatabaseClockFeatureTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task ReadClockExpiryUsesOneParameterizedSampleAndSupportsLongDurations()
    {
        using var logs = new ClockCommandLogProvider();
        using var loggerFactory = CreateLoggerFactory(logs);
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(fixture.ConnectionString);
        dataSourceBuilder.UseLoggerFactory(loggerFactory);
        await using var dataSource = dataSourceBuilder.Build();
        await using var context = CreateClockContext(dataSource);

        var duration = TimeSpan.FromDays(30);
        Assert.True(duration.TotalMilliseconds > int.MaxValue);
        var result = await context.Database.ReadClockExpiryAsync(duration);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.Zero, result.Value.SampledAt.Offset);
        Assert.Equal(TimeSpan.Zero, result.Value.ExpiresAt.Offset);
        Assert.Equal(0, result.Value.SampledAt.Ticks % 10);
        Assert.Equal(0, result.Value.ExpiresAt.Ticks % 10);
        Assert.Equal(duration, result.Value.ExpiresAt - result.Value.SampledAt);

        var command = Assert.Single(logs.ExecutedCommands);
        Assert.Contains("WITH sample AS MATERIALIZED", command, StringComparison.Ordinal);
        Assert.Contains("$1", command, StringComparison.Ordinal);
        Assert.Contains("TIMESTAMPTZ '9999-12-31 23:59:59.999999+00'", command, StringComparison.Ordinal);
        Assert.Equal(1, Count(command, "clock_timestamp()"));
    }

    [Fact]
    public async Task ReadClockExpiryReturnsNullForLargestWholeMillisecondDuration()
    {
        await using var context = CreateClockContext();
        var ticks = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond;
        var maximumWholeMilliseconds = TimeSpan.FromTicks(ticks);

        Assert.Null(await context.Database.ReadClockExpiryAsync(maximumWholeMilliseconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10000)]
    [InlineData(1)]
    [InlineData(15000)]
    public async Task ReadClockExpiryRejectsInvalidDurationsBeforeIssuingACommand(long ticks)
    {
        using var logs = new ClockCommandLogProvider();
        using var loggerFactory = CreateLoggerFactory(logs);
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(fixture.ConnectionString);
        dataSourceBuilder.UseLoggerFactory(loggerFactory);
        await using var dataSource = dataSourceBuilder.Build();
        await using var context = CreateClockContext(dataSource);
        var duration = TimeSpan.FromTicks(ticks);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => context.Database.ReadClockExpiryAsync(duration));

        Assert.Empty(logs.ExecutedCommands);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task ReadClockExpiryPreservesClosedOpenBorrowedAndRolledBackConnectionOwnership()
    {
        await using var closedContext = CreateClockContext();
        var closedConnection = closedContext.Database.GetDbConnection();
        Assert.Equal(ConnectionState.Closed, closedConnection.State);
        Assert.NotNull(await closedContext.Database.ReadClockExpiryAsync(TimeSpan.FromMinutes(1)));
        Assert.Equal(ConnectionState.Closed, closedConnection.State);

        await using var openContext = CreateClockContext();
        var openConnection = openContext.Database.GetDbConnection();
        await openContext.Database.OpenConnectionAsync();
        Assert.NotNull(await openContext.Database.ReadClockExpiryAsync(TimeSpan.FromMinutes(1)));
        Assert.Equal(ConnectionState.Open, openConnection.State);
        await openContext.Database.CloseConnectionAsync();
        Assert.Equal(ConnectionState.Closed, openConnection.State);

        await using var borrowedConnection = new NpgsqlConnection(fixture.ConnectionString);
        await borrowedConnection.OpenAsync();
        await using var borrowedTransaction = await borrowedConnection.BeginTransactionAsync();
        var borrowedOptions = new DbContextOptionsBuilder<ClockFeatureContext>()
            .UseProviderTestWarnings().UseNpgsql(borrowedConnection).Options;
        await using var borrowedContext = new ClockFeatureContext(borrowedOptions);
        await borrowedContext.Database.UseTransactionAsync(borrowedTransaction);

        Assert.NotNull(await borrowedContext.Database.ReadClockExpiryAsync(TimeSpan.FromMinutes(1)));
        Assert.Same(borrowedTransaction, borrowedContext.Database.CurrentTransaction!.GetDbTransaction());
        Assert.Equal(ConnectionState.Open, borrowedConnection.State);
        await borrowedTransaction.RollbackAsync();
        Assert.Equal(ConnectionState.Open, borrowedConnection.State);

        await using var rollbackContext = CreateClockContext();
        await using var transaction = await rollbackContext.Database.BeginTransactionAsync();
        Assert.NotNull(await rollbackContext.Database.ReadClockExpiryAsync(TimeSpan.FromMinutes(1)));
        Assert.Same(transaction, rollbackContext.Database.CurrentTransaction);
        await transaction.RollbackAsync();
        Assert.Null(rollbackContext.Database.CurrentTransaction);
    }

    [Fact]
    public async Task ReadClockExpiryPropagatesCancellationAndNativePostgreSqlErrors()
    {
        await using (var canceledContext = CreateClockContext())
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => canceledContext.Database.ReadClockExpiryAsync(TimeSpan.FromMinutes(1), canceled.Token));
            Assert.Equal(ConnectionState.Closed, canceledContext.Database.GetDbConnection().State);
        }

        await using var failedContext = CreateClockContext();
        await using var transaction = await failedContext.Database.BeginTransactionAsync();
        var connection = failedContext.Database.GetDbConnection();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "SELECT 1 / 0";
            var cause = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            Assert.Equal("22012", cause.SqlState);
        }

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => failedContext.Database.ReadClockExpiryAsync(TimeSpan.FromMinutes(1)));
        Assert.Equal("25P02", failure.SqlState);
        Assert.Same(transaction, failedContext.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Open, connection.State);
        await transaction.RollbackAsync();
        Assert.Null(failedContext.Database.CurrentTransaction);
    }

    [Fact]
    public async Task ClockTimestampOffsetMapsToTimestampPredicatesAndExecuteUpdateSetters()
    {
        await EnsureTimestampTableAsync();
        await using var setupContext = CreateClockContext();
        await setupContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE clock_tests.\"TimestampRows\"; INSERT INTO clock_tests.\"TimestampRows\" (\"Id\", \"ExpiresAt\", \"NullableExpiresAt\") VALUES (1, TIMESTAMPTZ '2000-01-01 00:00:00+00', TIMESTAMPTZ '2000-01-01 00:00:00+00')");

        using var logs = new ClockCommandLogProvider();
        using var loggerFactory = CreateLoggerFactory(logs);
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(fixture.ConnectionString);
        dataSourceBuilder.UseLoggerFactory(loggerFactory);
        await using var dataSource = dataSourceBuilder.Build();
        await using var context = CreateClockContext(dataSource);

        Assert.Throws<NotSupportedException>(() => DatabaseClock.ClockTimestampOffset());

        var beforeNonNullableUpdate = await context.Database.ReadClockTimestampAsync();
        logs.Clear();
        var nonNullableUpdated = await context.TimestampRows
            .Where(row => row.ExpiresAt < DatabaseClock.ClockTimestampOffset())
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                row => row.ExpiresAt,
                _ => DatabaseClock.ClockTimestampOffset()));
        Assert.Equal(1, nonNullableUpdated);
        Assert.Equal(2, Count(Assert.Single(logs.ExecutedCommands), "clock_timestamp()"));

        var beforeNullableUpdate = await context.Database.ReadClockTimestampAsync();
        logs.Clear();
        var nullableUpdated = await context.TimestampRows
            .Where(row => row.NullableExpiresAt.HasValue
                && row.NullableExpiresAt.Value < DatabaseClock.ClockTimestampOffset())
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                row => row.NullableExpiresAt,
                _ => (DateTimeOffset?)DatabaseClock.ClockTimestampOffset()));
        Assert.Equal(1, nullableUpdated);
        Assert.Equal(2, Count(Assert.Single(logs.ExecutedCommands), "clock_timestamp()"));

        var afterUpdates = await context.Database.ReadClockTimestampAsync();
        var updated = await context.TimestampRows.AsNoTracking().SingleAsync();
        Assert.InRange(updated.ExpiresAt, beforeNonNullableUpdate, afterUpdates);
        Assert.InRange(updated.NullableExpiresAt!.Value, beforeNullableUpdate, afterUpdates);
    }

    private ClockFeatureContext CreateClockContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<ClockFeatureContext>().UseProviderTestWarnings()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(interceptors);
        return new ClockFeatureContext(builder.Options);
    }

    private static ClockFeatureContext CreateClockContext(NpgsqlDataSource dataSource)
        => new(new DbContextOptionsBuilder<ClockFeatureContext>()
            .UseProviderTestWarnings().UseNpgsql(dataSource).Options);

    private async Task EnsureTimestampTableAsync()
    {
        await using var context = CreateClockContext();
        await context.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS clock_tests");
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS clock_tests."TimestampRows" (
                "Id" integer PRIMARY KEY,
                "ExpiresAt" timestamp with time zone NOT NULL,
                "NullableExpiresAt" timestamp with time zone NULL
            )
            """);
    }

    private static ILoggerFactory CreateLoggerFactory(ILoggerProvider provider)
        => LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddProvider(provider);
        });

    private static int Count(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }
}

internal sealed class ClockCommandLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _commands = new();

    public IReadOnlyList<string> ExecutedCommands => _commands.ToArray();

    public ILogger CreateLogger(string categoryName) => new ClockCommandLogger(categoryName, _commands);

    public void Clear()
    {
        while (_commands.TryDequeue(out _)) { }
    }

    public void Dispose() { }

    private sealed class ClockCommandLogger(string category, ConcurrentQueue<string> commands) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) && category == "Npgsql.Command" && eventId.Id == 2001)
            {
                commands.Enqueue(formatter(state, exception));
            }
        }
    }
}
