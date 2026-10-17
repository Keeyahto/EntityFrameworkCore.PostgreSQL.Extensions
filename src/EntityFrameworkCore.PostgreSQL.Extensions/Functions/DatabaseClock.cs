using System.Reflection;
using Microsoft.EntityFrameworkCore;

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
