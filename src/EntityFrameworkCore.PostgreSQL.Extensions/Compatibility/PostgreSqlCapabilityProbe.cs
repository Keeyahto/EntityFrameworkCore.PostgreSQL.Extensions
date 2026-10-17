using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

/// <summary>Outcome of comparing declared PostgreSQL capability requirements with one connected database.</summary>
public enum PostgreSqlCapabilityOutcome
{
    /// <summary>Every declared capability was observed and matched.</summary>
    Compatible,

    /// <summary>The provider or at least one declared capability does not match.</summary>
    Incompatible,

    /// <summary>The database facts could not be read reliably.</summary>
    Indeterminate
}

/// <summary>A required PostgreSQL extension and optional exact installed version.</summary>
/// <param name="Name">The extension name as stored in <c>pg_catalog.pg_extension</c>.</param>
/// <param name="RequiredVersion">An optional exact version string required by the caller.</param>
public sealed record PostgreSqlExtensionRequirement(string Name, string? RequiredVersion = null);

/// <summary>An installed PostgreSQL extension observed in one database.</summary>
/// <param name="Name">The installed extension name.</param>
/// <param name="Version">The installed extension version.</param>
public sealed record InstalledPostgreSqlExtension(string Name, string Version);

/// <summary>Read-only compatibility facts for one PostgreSQL database check.</summary>
/// <param name="Outcome">The typed compatibility outcome.</param>
/// <param name="SqlTargetVersion">The configured Npgsql SQL-generation target.</param>
/// <param name="ActualServerVersion">The version reported by the opened Npgsql connection.</param>
/// <param name="InstalledExtensions">Extensions installed in the current database.</param>
/// <param name="UnmetRequirements">Declared extension requirements that were not satisfied.</param>
/// <param name="FailureType">The exception type when database facts could not be read.</param>
public sealed record PostgreSqlCapabilityReport(
    PostgreSqlCapabilityOutcome Outcome,
    Version? SqlTargetVersion,
    Version? ActualServerVersion,
    IReadOnlyList<InstalledPostgreSqlExtension> InstalledExtensions,
    IReadOnlyList<PostgreSqlExtensionRequirement> UnmetRequirements,
    string? FailureType = null);

/// <summary>Read-only discovery of actual PostgreSQL and declared extension requirements.</summary>
public static class PostgreSqlCapabilityProbe
{
    /// <summary>
    /// Reads the effective Npgsql SQL target, connected server version and installed database extensions, then
    /// compares only the requirements supplied by the caller. This method never creates or installs extensions.
    /// </summary>
    /// <param name="context">An Npgsql-backed context.</param>
    /// <param name="requirements">The extension capabilities required by the calling host.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A compatibility report. Connection and catalog failures are indeterminate; caller cancellation propagates.</returns>
    public static async Task<PostgreSqlCapabilityReport> CheckAsync(
        DbContext context,
        IEnumerable<PostgreSqlExtensionRequirement> requirements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirements);

        var required = requirements.ToArray();
        if (required.Any(requirement => requirement is null || string.IsNullOrWhiteSpace(requirement.Name)))
        {
            throw new ArgumentException("Every PostgreSQL extension requirement needs a non-empty name.", nameof(requirements));
        }

        if (required.Select(requirement => requirement.Name).Distinct(StringComparer.Ordinal).Count() != required.Length)
        {
            throw new ArgumentException("A PostgreSQL extension requirement may be listed only once.", nameof(requirements));
        }

        if (!string.Equals(context.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            return new PostgreSqlCapabilityReport(
                PostgreSqlCapabilityOutcome.Incompatible,
                null,
                null,
                Array.Empty<InstalledPostgreSqlExtension>(),
                required,
                "UnsupportedProvider");
        }

        if (context.Database.GetDbConnection() is not NpgsqlConnection connection)
        {
            return new PostgreSqlCapabilityReport(
                PostgreSqlCapabilityOutcome.Incompatible,
                null,
                null,
                Array.Empty<InstalledPostgreSqlExtension>(),
                required,
                "UnsupportedConnection");
        }

        var sqlTarget = GetSqlTargetVersion(context);
        var wasOpened = false;
        Version? actualServerVersion = null;
        try
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            wasOpened = true;
            actualServerVersion = connection.PostgreSqlVersion;

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT extname, extversion FROM pg_catalog.pg_extension ORDER BY extname";
            if (context.Database.CurrentTransaction is { } transaction)
            {
                var currentTransaction = transaction.GetDbTransaction();
                if (currentTransaction is not NpgsqlTransaction npgsqlTransaction
                    || !ReferenceEquals(npgsqlTransaction.Connection, connection))
                {
                    throw new InvalidOperationException("The current EF transaction is not attached to the observed Npgsql connection.");
                }

                command.Transaction = npgsqlTransaction;
            }

            var installed = new List<InstalledPostgreSqlExtension>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    installed.Add(new InstalledPostgreSqlExtension(reader.GetString(0), reader.GetString(1)));
                }
            }

            var installedByName = installed.ToDictionary(extension => extension.Name, StringComparer.Ordinal);
            var unmet = required.Where(requirement =>
                !installedByName.TryGetValue(requirement.Name, out var actual)
                || (requirement.RequiredVersion is not null
                    && !string.Equals(requirement.RequiredVersion, actual.Version, StringComparison.Ordinal))).ToArray();
            var sqlTargetExceedsServer = sqlTarget is not null
                && actualServerVersion is not null
                && sqlTarget > actualServerVersion;

            return new PostgreSqlCapabilityReport(
                unmet.Length == 0 && !sqlTargetExceedsServer
                    ? PostgreSqlCapabilityOutcome.Compatible
                    : PostgreSqlCapabilityOutcome.Incompatible,
                sqlTarget,
                actualServerVersion,
                installed,
                unmet);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return new PostgreSqlCapabilityReport(
                PostgreSqlCapabilityOutcome.Indeterminate,
                sqlTarget,
                actualServerVersion,
                Array.Empty<InstalledPostgreSqlExtension>(),
                required,
                exception.GetType().Name);
        }
        finally
        {
            if (wasOpened)
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

#pragma warning disable EF1001 // This single provider-specific probe reads Npgsql's configured SQL target without mutating it.
    private static Version GetSqlTargetVersion(DbContext context)
    {
        var options = context.GetService<IDbContextOptions>().Extensions.OfType<NpgsqlOptionsExtension>().SingleOrDefault();
        return options?.PostgresVersion ?? NpgsqlOptionsExtension.DefaultPostgresVersion;
    }
#pragma warning restore EF1001
}
