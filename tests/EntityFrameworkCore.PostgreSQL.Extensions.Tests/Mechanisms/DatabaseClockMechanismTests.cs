using System.Collections.Concurrent;
using System.Data.Common;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using Npgsql;
using Xunit;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Mechanisms;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
[Trait("MechanismTestCategory", "PostgreSQL")]
public sealed class DatabaseClockMechanismTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task MappedClockTimestampAdvancesWhileNowRemainsFixedWithinOneTransaction()
    {
        await using var context = CreateClockContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var transactionNow1 = await ReadTransactionNowAsync(context);
        var clock1 = await ReadMappedClockAsync(context);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        var transactionNow2 = await ReadTransactionNowAsync(context);
        var clock2 = await ReadMappedClockAsync(context);

        Assert.Equal(DateTimeKind.Utc, clock1.Kind);
        Assert.Equal(DateTimeKind.Utc, clock2.Kind);
        Assert.Equal(transactionNow1, transactionNow2);
        Assert.True(clock2 > clock1, $"clock_timestamp did not advance: {clock1:O} then {clock2:O}.");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task DirectClientInvocationThrowsAndOneNativeSampleCanBeReused()
    {
        Assert.Throws<NotSupportedException>(() => DatabaseClock.ClockTimestamp());
        await using var context = CreateClockContext();

        var sample = await context.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        var createdAt = sample;
        var expiresAt = sample.AddMinutes(5);

        Assert.Equal(DateTimeKind.Utc, sample.Kind);
        Assert.Equal(sample, createdAt);
        Assert.Equal(TimeSpan.FromMinutes(5), expiresAt - createdAt);
    }

    [Fact]
    public async Task ReadClockTimestampBalancesClosedOpenBorrowedAndRolledBackConnectionLifetimes()
    {
        await using var closedContext = CreateClockContext();
        var closedConnection = closedContext.Database.GetDbConnection();
        Assert.Equal(System.Data.ConnectionState.Closed, closedConnection.State);
        var closedSample = await closedContext.Database.ReadClockTimestampAsync();
        AssertClockSample(closedSample);
        Assert.Equal(System.Data.ConnectionState.Closed, closedConnection.State);

        await using var openContext = CreateClockContext();
        var openConnection = openContext.Database.GetDbConnection();
        await openContext.Database.OpenConnectionAsync();
        var openSample = await openContext.Database.ReadClockTimestampAsync();
        AssertClockSample(openSample);
        Assert.Equal(System.Data.ConnectionState.Open, openConnection.State);
        await openContext.Database.CloseConnectionAsync();
        Assert.Equal(System.Data.ConnectionState.Closed, openConnection.State);

        await using var borrowedConnection = new NpgsqlConnection(fixture.ConnectionString);
        await borrowedConnection.OpenAsync();
        await using var borrowedTransaction = await borrowedConnection.BeginTransactionAsync();
        var borrowedOptions = new DbContextOptionsBuilder<DatabaseClockContext>()
            .UseProviderTestWarnings().UseNpgsql(borrowedConnection).Options;
        await using var borrowedContext = new DatabaseClockContext(borrowedOptions);
        await borrowedContext.Database.UseTransactionAsync(borrowedTransaction);
        var borrowedSample = await borrowedContext.Database.ReadClockTimestampAsync();
        AssertClockSample(borrowedSample);
        Assert.Same(borrowedTransaction, borrowedContext.Database.CurrentTransaction!.GetDbTransaction());
        Assert.Equal(System.Data.ConnectionState.Open, borrowedConnection.State);
        await borrowedTransaction.RollbackAsync();
        Assert.Equal(System.Data.ConnectionState.Open, borrowedConnection.State);

        await using var rollbackContext = CreateClockContext();
        await using var transaction = await rollbackContext.Database.BeginTransactionAsync();
        var rollbackSample = await rollbackContext.Database.ReadClockTimestampAsync();
        AssertClockSample(rollbackSample);
        await transaction.RollbackAsync();
        Assert.Null(rollbackContext.Database.CurrentTransaction);
    }

    [Fact]
    public async Task ReadClockTimestampCancellationPropagatesAndCleansUpAnOpenedConnection()
    {
        await using (var canceledBeforeOpen = CreateClockContext())
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => canceledBeforeOpen.Database.ReadClockTimestampAsync(canceled.Token));
            Assert.Equal(System.Data.ConnectionState.Closed, canceledBeforeOpen.Database.GetDbConnection().State);
        }

        using var cancelWhenOpen = new CancellationTokenSource();
        await using var interrupted = CreateClockContext(new CancelClockReadAfterOpenInterceptor(cancelWhenOpen));
        var connection = interrupted.Database.GetDbConnection();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => interrupted.Database.ReadClockTimestampAsync(cancelWhenOpen.Token));
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.Null(interrupted.Database.CurrentTransaction);
    }

    [Fact]
    public async Task ReadClockTimestampProviderFailurePreservesAndBalancesCallerTransaction()
    {
        await using var context = CreateClockContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var connection = context.Database.GetDbConnection();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "SELECT 1 / 0";
            var cause = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            Assert.Equal("22012", cause.SqlState);
        }

        var failure = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ReadClockTimestampAsync());
        Assert.Equal("25P02", failure.SqlState);
        Assert.Same(transaction, context.Database.CurrentTransaction);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        await transaction.RollbackAsync();
        Assert.Null(context.Database.CurrentTransaction);
    }

    [Fact]
    public async Task ReadClockTimestampRequiresNpgsqlAndDoesNotNeedTheQueryPlugin()
    {
        await using var wrongProvider = new DbContext(new DbContextOptionsBuilder<DbContext>()
            .UseProviderTestWarnings().UseSqlite("Data Source=:memory:").Options);
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => wrongProvider.Database.ReadClockTimestampAsync());
        Assert.Contains("Npgsql EF Core provider", exception.Message, StringComparison.Ordinal);
        Assert.Equal(System.Data.ConnectionState.Closed, wrongProvider.Database.GetDbConnection().State);

        await using var noQueryPlugin = CreateClockContext();
        var value = await noQueryPlugin.Database.ReadClockTimestampAsync();
        AssertClockSample(value);
    }

    [Fact]
    public async Task ReadClockTimestampReturnsOneUtcDateTimeWithNodaTimeInEitherRegistrationOrder()
    {
        using var nodaTimeThenExtensionsLogs = new NpgsqlCommandLogProvider();
        using var nodaTimeThenExtensionsLoggerFactory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddProvider(nodaTimeThenExtensionsLogs);
        });
        var nodaTimeThenExtensionsDataSourceBuilder = new NpgsqlDataSourceBuilder(fixture.ConnectionString);
        nodaTimeThenExtensionsDataSourceBuilder.UseLoggerFactory(nodaTimeThenExtensionsLoggerFactory);
        nodaTimeThenExtensionsDataSourceBuilder.UseNodaTime();
        await using var nodaTimeThenExtensionsDataSource = nodaTimeThenExtensionsDataSourceBuilder.Build();
        var nodaTimeThenExtensionsOptions = new DbContextOptionsBuilder<DatabaseClockContext>()
            .UseProviderTestWarnings()
            .UseNpgsql(nodaTimeThenExtensionsDataSource, npgsql =>
            {
                npgsql.UseNodaTime();
                npgsql.UsePostgreSqlExtensions();
            })
            .Options;
        await using var nodaTimeThenExtensions = new DatabaseClockContext(nodaTimeThenExtensionsOptions);
        var closedConnection = nodaTimeThenExtensions.Database.GetDbConnection();
        Assert.Equal(System.Data.ConnectionState.Closed, closedConnection.State);

        var firstSample = await nodaTimeThenExtensions.Database.ReadClockTimestampAsync();

        AssertClockSample(firstSample);
        Assert.Equal(System.Data.ConnectionState.Closed, closedConnection.State);
        Assert.Single(nodaTimeThenExtensionsLogs.CompletedClockCommands);

        using var extensionsThenNodaTimeLogs = new NpgsqlCommandLogProvider();
        using var extensionsThenNodaTimeLoggerFactory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddProvider(extensionsThenNodaTimeLogs);
        });
        var extensionsThenNodaTimeDataSourceBuilder = new NpgsqlDataSourceBuilder(fixture.ConnectionString);
        extensionsThenNodaTimeDataSourceBuilder.UseLoggerFactory(extensionsThenNodaTimeLoggerFactory);
        extensionsThenNodaTimeDataSourceBuilder.UseNodaTime();
        await using var extensionsThenNodaTimeDataSource = extensionsThenNodaTimeDataSourceBuilder.Build();
        var extensionsThenNodaTimeOptions = new DbContextOptionsBuilder<DatabaseClockContext>()
            .UseProviderTestWarnings()
            .UseNpgsql(extensionsThenNodaTimeDataSource, npgsql =>
            {
                npgsql.UsePostgreSqlExtensions();
                npgsql.UseNodaTime();
            })
            .Options;
        await using var extensionsThenNodaTime = new DatabaseClockContext(extensionsThenNodaTimeOptions);
        var openConnection = extensionsThenNodaTime.Database.GetDbConnection();
        await extensionsThenNodaTime.Database.OpenConnectionAsync();
        await using var transaction = await extensionsThenNodaTime.Database.BeginTransactionAsync();
        var currentTransaction = extensionsThenNodaTime.Database.CurrentTransaction;

        var secondSample = await extensionsThenNodaTime.Database.ReadClockTimestampAsync();

        AssertClockSample(secondSample);
        Assert.Same(currentTransaction, extensionsThenNodaTime.Database.CurrentTransaction);
        Assert.Equal(System.Data.ConnectionState.Open, openConnection.State);
        Assert.Single(extensionsThenNodaTimeLogs.CompletedClockCommands);
        await transaction.RollbackAsync();
        Assert.Null(extensionsThenNodaTime.Database.CurrentTransaction);
        Assert.Equal(System.Data.ConnectionState.Open, openConnection.State);
        await extensionsThenNodaTime.Database.CloseConnectionAsync();
        Assert.Equal(System.Data.ConnectionState.Closed, openConnection.State);
    }

    private DatabaseClockContext CreateClockContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<DatabaseClockContext>().UseProviderTestWarnings()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(interceptors);
        return new DatabaseClockContext(builder.Options);
    }

    private static void AssertClockSample(DateTime value)
    {
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(0, value.Ticks % 10);
    }

    private static Task<DateTime> ReadMappedClockAsync(DatabaseClockContext context)
        => context.Samples.FromSqlRaw("SELECT 1 AS \"Id\"")
            .Select(_ => DatabaseClock.ClockTimestamp())
            .SingleAsync();

    private static Task<DateTime> ReadTransactionNowAsync(DatabaseClockContext context)
        => context.Database.SqlQuery<DateTime>($"SELECT now() AS \"Value\"").SingleAsync();
}

internal sealed class CancelClockReadAfterOpenInterceptor(CancellationTokenSource cancellation)
    : DbConnectionInterceptor
{
    public override Task ConnectionOpenedAsync(
        System.Data.Common.DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        cancellation.Cancel();
        return Task.CompletedTask;
    }
}

internal sealed class NpgsqlCommandLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<NpgsqlCommandLogEvent> _events = new();

    public IReadOnlyList<NpgsqlCommandLogEvent> CompletedClockCommands => _events
        .Where(logEvent => logEvent.Category == "Npgsql.Command"
            && logEvent.EventId == 2001
            && logEvent.Message.Contains("SELECT pg_catalog.clock_timestamp()", StringComparison.Ordinal))
        .ToArray();

    public ILogger CreateLogger(string categoryName) => new NpgsqlCommandLogger(categoryName, _events);

    public void Dispose() { }

    private sealed class NpgsqlCommandLogger(string category, ConcurrentQueue<NpgsqlCommandLogEvent> events) : ILogger
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
            if (IsEnabled(logLevel))
            {
                events.Enqueue(new NpgsqlCommandLogEvent(category, eventId.Id, formatter(state, exception)));
            }
        }
    }
}

internal sealed record NpgsqlCommandLogEvent(string Category, int EventId, string Message);

internal sealed class DatabaseClockContext(DbContextOptions<DatabaseClockContext> options) : DbContext(options)
{
    public DbSet<ClockSample> Samples => Set<ClockSample>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClockSample>().HasNoKey();
        modelBuilder.MapClockTimestamp();
    }
}

internal sealed class ClockSample
{
    public int Id { get; set; }
}
