using EntityFrameworkCore.PostgreSQL.Extensions;
using Microsoft.EntityFrameworkCore;

internal static partial class Program
{
    private static async Task RunOrderedSetAndExpiryAsync(string connectionString)
    {
        var observer = new SelectObserver();
        var options = new DbContextOptionsBuilder<SmokeContext>()
            .UseNpgsql(connectionString, provider =>
            {
                provider.UsePostgreSqlExtensions();
                provider.UseNodaTime();
            })
            .AddInterceptors(observer).Options;
        await using var context = new SmokeContext(options);
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        context.UuidRecords.AddRange(
            new UuidRecord { Id = first, ParentId = second },
            new UuidRecord { Id = second, ParentId = first });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var compiled = EF.CompileAsyncQuery((SmokeContext db, Guid[] ids) => db.UuidRecords
            .AsNoTracking().Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.ParentId).ThenBy(row => row.Id).ForUpdate());
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var ids = new[] { second, first, first, Guid.NewGuid() };
            observer.Clear();
            var rows = await context.UuidRecords.AsNoTracking()
                .Where(row => ids.Contains(row.Id)).OrderBy(row => row.Id).ForUpdate().ToListAsync();
            Require(rows.Select(row => row.Id).SequenceEqual(new[] { first, second }),
                "The packaged UUID set did not preserve membership and primary-key order.");
            Require(observer.Commands.Count == 1 && observer.Commands[0].Contains("FOR UPDATE OF", StringComparison.Ordinal),
                "The packaged UUID set was not one locked statement.");

            var compiledRows = new List<UuidRecord>();
            await foreach (var row in compiled(context, ids)) compiledRows.Add(row);
            Require(compiledRows.Select(row => row.Id).SequenceEqual(new[] { second, first }),
                "The packaged compiled UUID set did not preserve parent/key order.");
            await foreach (var row in compiled(context, Array.Empty<Guid>()))
                throw new InvalidOperationException("The packaged compiled empty UUID set returned a row.");
            await transaction.RollbackAsync();
        }

        var expiry = await context.Database.ReadClockExpiryAsync(TimeSpan.FromDays(30));
        Require(expiry is { } pair && pair.ExpiresAt - pair.SampledAt == TimeSpan.FromDays(30)
            && pair.SampledAt.Offset == TimeSpan.Zero && pair.ExpiresAt.Offset == TimeSpan.Zero,
            "The packaged expiry did not preserve a shared UTC sample and long duration.");
        var largestMilliseconds = TimeSpan.FromTicks(TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond);
        Require(await context.Database.ReadClockExpiryAsync(largestMilliseconds) is null,
            "The packaged expiry accepted a timestamp beyond the representable bound.");

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var changed = await context.UuidRecords
                .Where(row => row.Id == first && row.ReleasedAt == null
                    && row.ExpiresAt > DatabaseClock.ClockTimestampOffset())
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReleasedAt,
                    _ => DatabaseClock.ClockTimestampOffset()));
            Require(changed == 1, "The packaged offset clock predicate/update did not affect the live row.");
            Require((await context.UuidRecords.AsNoTracking().SingleAsync(row => row.Id == first)).ReleasedAt is { Offset: var offset }
                && offset == TimeSpan.Zero, "The packaged offset clock setter did not store a UTC timestamp.");
            await transaction.RollbackAsync();
        }
        Require(!await context.UuidRecords.AnyAsync(row => row.ReleasedAt != null),
            "The packaged clock update escaped the caller rollback.");
        Checks.Add("ordered-uuid-set:complete-single-two-column-compiled-empty;clock-expiry:shared-long-overflow;offset-clock:predicate-update-rollback");
    }
}

internal sealed class UuidRecord
{
    public Guid Id { get; set; }
    public Guid ParentId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } = new(9998, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset? ReleasedAt { get; set; }
}
