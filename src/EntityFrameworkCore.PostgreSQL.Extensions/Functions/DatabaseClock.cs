using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

/// <summary>PostgreSQL database-clock functions available to translated EF queries.</summary>
public static class DatabaseClock
{
    /// <summary>Returns PostgreSQL wall-clock time at the function evaluation point.</summary>
    /// <remarks>Map this method with <see cref="PostgreSqlClockModelBuilderExtensions.MapClockTimestamp"/>. Direct client invocation is unsupported.</remarks>
    /// <exception cref="NotSupportedException">The method was invoked on the client.</exception>
    public static DateTime ClockTimestamp()
        => throw new NotSupportedException("ClockTimestamp is only supported inside an EF query translated by PostgreSQL.");

    /// <summary>Returns PostgreSQL wall-clock time as a <see cref="DateTimeOffset"/> expression.</summary>
    /// <remarks>Map this method with <see cref="PostgreSqlClockModelBuilderExtensions.MapClockTimestamp"/>. Direct client invocation is unsupported.</remarks>
    /// <exception cref="NotSupportedException">The method was invoked on the client.</exception>
    public static DateTimeOffset ClockTimestampOffset()
        => throw new NotSupportedException("ClockTimestampOffset is only supported inside an EF query translated by PostgreSQL.");
}

/// <summary>Maps both PostgreSQL database-clock functions into an EF model.</summary>
public static class PostgreSqlClockModelBuilderExtensions
{
    private static readonly MethodInfo ClockTimestampMethod = typeof(DatabaseClock)
        .GetMethod(nameof(DatabaseClock.ClockTimestamp), BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo ClockTimestampOffsetMethod = typeof(DatabaseClock)
        .GetMethod(nameof(DatabaseClock.ClockTimestampOffset), BindingFlags.Public | BindingFlags.Static)!;

    /// <summary>Maps <see cref="DatabaseClock.ClockTimestamp"/> to PostgreSQL <c>pg_catalog.clock_timestamp()</c>.</summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    public static ModelBuilder MapClockTimestamp(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDbFunction(ClockTimestampMethod)
            .HasName("clock_timestamp")
            .HasSchema("pg_catalog");
        modelBuilder.HasDbFunction(ClockTimestampOffsetMethod)
            .HasName("clock_timestamp")
            .HasSchema("pg_catalog");
        return modelBuilder;
    }
}

/// <summary>Reads a single value from PostgreSQL's wall clock.</summary>
public static class PostgreSqlClockDatabaseExtensions
{
    /// <summary>Reads one UTC timestamp from <c>pg_catalog.clock_timestamp()</c>.</summary>
    /// <param name="database">The Npgsql-backed database facade.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The server timestamp at PostgreSQL precision.</returns>
    /// <exception cref="NotSupportedException">The database does not use the Npgsql EF Core provider and connection.</exception>
    /// <exception cref="InvalidOperationException">An active EF transaction is not attached to the Npgsql connection, or the provider did not return a UTC timestamp.</exception>
    public static async Task<DateTime> ReadClockTimestampAsync(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var connection = PostgreSqlCommandGuard.GetConnection(database, nameof(ReadClockTimestampAsync));
        var transaction = PostgreSqlCommandGuard.GetCurrentTransaction(
            database,
            connection,
            nameof(ReadClockTimestampAsync),
            required: false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.clock_timestamp()";
        command.Transaction = transaction;

        await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("PostgreSQL did not return pg_catalog.clock_timestamp().");
            }

            var timestamp = await reader.GetFieldValueAsync<DateTime>(0, cancellationToken).ConfigureAwait(false);
            if (timestamp.Kind != DateTimeKind.Utc)
            {
                throw new InvalidOperationException("Npgsql did not return pg_catalog.clock_timestamp() as a UTC DateTime value.");
            }

            return timestamp;
        }
        finally
        {
            await database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads one PostgreSQL wall-clock sample and computes its expiry in the same statement.</summary>
    /// <param name="database">The Npgsql-backed database facade.</param>
    /// <param name="duration">A positive duration containing a whole number of milliseconds.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The UTC server sample and expiry, or <see langword="null"/> when the expiry exceeds <c>9999-12-31 23:59:59.999999+00</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is not a positive whole number of milliseconds.</exception>
    /// <exception cref="NotSupportedException">The database does not use the Npgsql EF Core provider and connection.</exception>
    /// <exception cref="InvalidOperationException">An active EF transaction is not attached to the Npgsql connection, or PostgreSQL did not return UTC timestamps.</exception>
    public static async Task<(DateTimeOffset SampledAt, DateTimeOffset ExpiresAt)?> ReadClockExpiryAsync(
        this DatabaseFacade database,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (duration.Ticks <= 0 || duration.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "The duration must be positive and contain a whole number of milliseconds.");
        }

        var durationMilliseconds = duration.Ticks / TimeSpan.TicksPerMillisecond;
        var connection = PostgreSqlCommandGuard.GetConnection(database, nameof(ReadClockExpiryAsync));
        var transaction = PostgreSqlCommandGuard.GetCurrentTransaction(
            database,
            connection,
            nameof(ReadClockExpiryAsync),
            required: false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH sample AS MATERIALIZED (
                SELECT pg_catalog.clock_timestamp() AS sampled_at
            ), calculated AS (
                SELECT sampled_at,
                       sampled_at + @duration_milliseconds * INTERVAL '1 millisecond' AS expires_at
                FROM sample
            )
            SELECT sampled_at,
                   CASE
                       WHEN expires_at > TIMESTAMPTZ '9999-12-31 23:59:59.999999+00' THEN NULL
                       ELSE expires_at
                   END AS expires_at
            FROM calculated
            """;
        command.Transaction = transaction;
        command.Parameters.Add(new NpgsqlParameter("duration_milliseconds", NpgsqlDbType.Bigint)
        {
            Value = durationMilliseconds
        });

        await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("PostgreSQL did not return a clock sample and expiry.");
            }

            var sampledAt = await reader.GetFieldValueAsync<DateTimeOffset>(0, cancellationToken).ConfigureAwait(false);
            if (sampledAt.Offset != TimeSpan.Zero)
            {
                throw new InvalidOperationException("Npgsql did not return the PostgreSQL clock sample with a zero UTC offset.");
            }

            if (await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var expiresAt = await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken).ConfigureAwait(false);
            if (expiresAt.Offset != TimeSpan.Zero)
            {
                throw new InvalidOperationException("Npgsql did not return the PostgreSQL expiry with a zero UTC offset.");
            }

            return (sampledAt, expiresAt);
        }
        finally
        {
            await database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
