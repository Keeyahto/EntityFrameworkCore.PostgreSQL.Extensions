using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

internal static class PostgreSqlCommandGuard
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    internal static NpgsqlConnection GetConnection(DatabaseFacade database, string operation)
    {
        if (!string.Equals(database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"{operation} requires the Npgsql EF Core provider.");
        }

        return database.GetDbConnection() as NpgsqlConnection
            ?? throw new NotSupportedException($"{operation} requires an Npgsql connection.");
    }

    internal static NpgsqlTransaction? GetCurrentTransaction(
        DatabaseFacade database,
        NpgsqlConnection connection,
        string operation,
        bool required)
    {
        var currentTransaction = database.CurrentTransaction;
        if (currentTransaction is null)
        {
            if (required)
            {
                throw new InvalidOperationException($"{operation} requires an existing caller-owned EF transaction.");
            }

            return null;
        }

        if (currentTransaction.GetDbTransaction() is not NpgsqlTransaction transaction
            || !ReferenceEquals(transaction.Connection, connection))
        {
            throw new InvalidOperationException($"{operation} requires the current EF transaction to be attached to the Npgsql connection.");
        }

        return transaction;
    }
}
