using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using EntityFrameworkCore.PostgreSQL.Extensions;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

internal sealed class SqlCommandObserver : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly ConcurrentQueue<IReadOnlyList<(string Name, object? Value)>> _parameters = new();

    public int Count => _commands.Count;

    public IReadOnlyList<string> Commands => _commands.ToArray();

    public IReadOnlyList<IReadOnlyList<(string Name, object? Value)>> Parameters => _parameters.ToArray();

    public void Clear()
    {
        while (_commands.TryDequeue(out _))
        {
        }

        while (_parameters.TryDequeue(out _))
        {
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command)
    {
        _commands.Enqueue(command.CommandText);
        _parameters.Enqueue(command.Parameters
            .Cast<DbParameter>()
            .Select(parameter => (parameter.ParameterName, parameter.Value is DBNull ? null : parameter.Value))
            .ToArray());
    }
}

internal static partial class QueryTestSupport
{
    private static int _environmentWritten;

    public static PostgresException AssertNativePostgresFailure(Exception wrapper, string sqlState)
    {
        var providerWrapper = Assert.IsType<InvalidOperationException>(wrapper);
        Assert.Contains("transient failure", providerWrapper.Message, StringComparison.OrdinalIgnoreCase);
        var postgresException = Assert.IsType<PostgresException>(providerWrapper.InnerException);
        Assert.Equal(sqlState, postgresException.SqlState);
        return postgresException;
    }

    public static async Task<object?> ExecuteScalarAsync(DbConnection connection, DbTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public static ProbeContext CreateContext(
        PostgreSqlFixture fixture,
        SqlCommandObserver? observer = null)
    {
        return CreateContext(fixture, enableExtensions: true, observer: observer);
    }

    public static ProbeContext CreateNativeContext(
        PostgreSqlFixture fixture,
        SqlCommandObserver? observer = null)
        => CreateContext(fixture, enableExtensions: false, observer: observer);

    private static ProbeContext CreateContext(
        PostgreSqlFixture fixture,
        bool enableExtensions,
        SqlCommandObserver? observer)
    {
        var builder = new DbContextOptionsBuilder<ProbeContext>().UseProviderTestWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql =>
            {
                if (enableExtensions)
                {
                    npgsql.UsePostgreSqlExtensions();
                }
            });

        if (observer is not null)
        {
            builder.AddInterceptors(observer);
        }

        return new ProbeContext(builder.Options);
    }

    public static async Task SeedAsync(PostgreSqlFixture fixture, params ProbeRow[] rows)
    {
        await fixture.ResetAsync();
        await using var context = fixture.CreateContext();
        context.Peers.AddRange(rows.Select(row => row.PeerId).Distinct()
            .Select(id => new PeerRow { Id = id, Payload = $"seed-peer-{id}" }));
        context.Rows.AddRange(rows);
        await context.SaveChangesAsync();
    }

    public static async Task SeedAsync(
        PostgreSqlFixture fixture,
        IReadOnlyCollection<ProbeRow> rows,
        IReadOnlyCollection<PeerRow> peers)
    {
        await fixture.ResetAsync();
        await using var context = fixture.CreateContext();
        context.Rows.AddRange(rows);
        context.Peers.AddRange(peers);
        var suppliedPeerIds = peers.Select(peer => peer.Id).ToHashSet();
        context.Peers.AddRange(rows.Select(row => row.PeerId).Distinct()
            .Where(id => !suppliedPeerIds.Contains(id))
            .Select(id => new PeerRow { Id = id, Payload = $"seed-peer-{id}" }));
        await context.SaveChangesAsync();
    }

    public static string RootAlias(string sql, string tableName = "Rows")
    {
        var match = Regex.Match(sql,
            $"""FROM "{Regex.Escape(tableName)}" AS (?<alias>"[^"]+"|[A-Za-z_][A-Za-z0-9_]*)""",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Expected a generated {tableName} alias in SQL: {sql}");
        return match.Groups["alias"].Value;
    }

    public static void WriteOutcome(
        Xunit.Abstractions.ITestOutputHelper output,
        string queryId,
        string disposition,
        string detail)
    {
        if (Interlocked.Exchange(ref _environmentWritten, 1) == 0)
        {
            output.WriteLine($"POSTGRES_ENV|image={PostgreSqlFixture.ImageReference};serverVersion={PostgreSqlFixture.ObservedServerVersion}");
        }

        output.WriteLine($"{queryId}|production|expected={disposition}|observed={disposition}|{detail}");
    }

}
