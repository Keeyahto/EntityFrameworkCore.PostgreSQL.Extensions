using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

/// <summary>PostgreSQL database-clock functions available to translated EF queries.</summary>
public static class DatabaseClock
{
    /// <summary>Returns PostgreSQL wall-clock time at the function evaluation point.</summary>
    /// <remarks>Map this method with <see cref="PostgreSqlClockModelBuilderExtensions.MapClockTimestamp"/>. Direct client invocation is unsupported.</remarks>
    /// <exception cref="NotSupportedException">The method was invoked on the client.</exception>
    public static DateTime ClockTimestamp()
        => throw new NotSupportedException("ClockTimestamp is only supported inside an EF query translated by PostgreSQL.");
}

/// <summary>Maps PostgreSQL database-clock functions into an EF model.</summary>
public static class PostgreSqlClockModelBuilderExtensions
{
    private static readonly MethodInfo ClockTimestampMethod = typeof(DatabaseClock)
        .GetMethod(nameof(DatabaseClock.ClockTimestamp), BindingFlags.Public | BindingFlags.Static)!;

    /// <summary>Maps <see cref="DatabaseClock.ClockTimestamp"/> to PostgreSQL <c>pg_catalog.clock_timestamp()</c>.</summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    public static ModelBuilder MapClockTimestamp(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDbFunction(ClockTimestampMethod)
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
}
