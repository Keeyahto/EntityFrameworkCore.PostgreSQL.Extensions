using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

/// <summary>Executes PostgreSQL transaction-scoped advisory locks in the current EF transaction.</summary>
public static class AdvisoryLockExtensions
{
    /// <summary>Acquires a transaction-scoped advisory lock using PostgreSQL's stable text hash.</summary>
    /// <param name="database">The database facade whose current transaction owns the lock.</param>
    /// <param name="key">The text key to hash on PostgreSQL.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <exception cref="InvalidOperationException">No current EF transaction exists.</exception>
    public static async Task AcquireTransactionAdvisoryLockAsync(
        this DatabaseFacade database,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(key);
        RequireCurrentTransaction(database);

        await database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Acquires a transaction-scoped advisory lock in PostgreSQL's bigint key space.</summary>
    /// <param name="database">The database facade whose current transaction owns the lock.</param>
    /// <param name="key">The 64-bit PostgreSQL advisory-lock key.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <exception cref="InvalidOperationException">No current EF transaction exists.</exception>
    public static async Task AcquireTransactionAdvisoryLockAsync(
        this DatabaseFacade database,
        long key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        RequireCurrentTransaction(database);

        await database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({key})",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Acquires a transaction-scoped advisory lock in PostgreSQL's two-int key space.</summary>
    /// <param name="database">The database facade whose current transaction owns the lock.</param>
    /// <param name="key1">The first 32-bit PostgreSQL advisory-lock key.</param>
    /// <param name="key2">The second 32-bit PostgreSQL advisory-lock key.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <exception cref="InvalidOperationException">No current EF transaction exists.</exception>
    public static async Task AcquireTransactionAdvisoryLockAsync(
        this DatabaseFacade database,
        int key1,
        int key2,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        RequireCurrentTransaction(database);

        await database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({key1}, {key2})",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks the current caller-owned PostgreSQL transaction read-only.</summary>
    /// <param name="database">The database facade whose current transaction is changed.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <exception cref="InvalidOperationException">No current EF transaction exists.</exception>
    public static async Task SetTransactionReadOnlyAsync(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        RequireCurrentTransaction(database);

        await database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
    }

    private static void RequireCurrentTransaction(DatabaseFacade database)
    {
        if (database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("This PostgreSQL operation requires an existing caller-owned EF transaction.");
        }
    }
}
