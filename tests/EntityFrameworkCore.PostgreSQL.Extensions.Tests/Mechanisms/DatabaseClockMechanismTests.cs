using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Mechanisms;

[Collection(PostgreSqlCollection.Name)]
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

    private DatabaseClockContext CreateClockContext()
        => new(new DbContextOptionsBuilder<DatabaseClockContext>().UsePocMatrixWarnings().UseNpgsql(fixture.ConnectionString).Options);

    private static Task<DateTime> ReadMappedClockAsync(DatabaseClockContext context)
        => context.Samples.FromSqlRaw("SELECT 1 AS \"Id\"")
            .Select(_ => DatabaseClock.ClockTimestamp())
            .SingleAsync();

    private static Task<DateTime> ReadTransactionNowAsync(DatabaseClockContext context)
        => context.Database.SqlQuery<DateTime>($"SELECT now() AS \"Value\"").SingleAsync();
}

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
