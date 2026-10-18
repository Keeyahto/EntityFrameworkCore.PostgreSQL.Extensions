using Microsoft.EntityFrameworkCore;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Mechanisms;

internal sealed class ClockFeatureContext(DbContextOptions<ClockFeatureContext> options) : DbContext(options)
{
    public DbSet<ClockTimestampRow> TimestampRows => Set<ClockTimestampRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.MapClockTimestamp();
        modelBuilder.Entity<ClockTimestampRow>(entity =>
        {
            entity.ToTable("TimestampRows", "clock_tests");
            entity.HasKey(row => row.Id);
        });
    }
}

internal sealed class ClockTimestampRow
{
    public int Id { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? NullableExpiresAt { get; set; }
}
