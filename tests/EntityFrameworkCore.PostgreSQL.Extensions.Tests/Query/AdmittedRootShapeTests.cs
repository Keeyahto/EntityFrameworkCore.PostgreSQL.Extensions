using System.Data.Common;
using System.Text.RegularExpressions;
using EntityFrameworkCore.PostgreSQL.Extensions;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class AdmittedRootShapeTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    public static TheoryData<PocCarrier> Candidates => new()
    {
        PocCarrier.TableAnnotations,
        PocCarrier.TranslatorTag,
        PocCarrier.ExpressionTag
    };

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task Q01_where_then_for_update_locks_only_the_selected_root_row(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new[]
            {
                new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "target" },
                new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "other" }
            },
            new[] { new PeerRow { Id = 7, Payload = "join-target" } });

        var observer = new SqlCommandObserver();
        await using var holder = QueryTestSupport.CreateContext(fixture, candidate, observer);
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        var holderBackend = await BackendIdAsync(holder.Database.GetDbConnection(), holderTransaction.GetDbTransaction());

        var query = holder.Rows.Where(row => row.Id == 1).ForUpdate();
        var generatedSql = query.ToQueryString();
        var selected = await query.SingleAsync();

        Assert.Equal(1, selected.Id);
        Assert.Single(observer.Commands);
        Assert.Contains($"FOR UPDATE OF {QueryTestSupport.RootAlias(generatedSql)}", generatedSql, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF", observer.Commands[0], StringComparison.Ordinal);
        Assert.Contains($"FOR UPDATE OF {QueryTestSupport.RootAlias(observer.Commands[0])}", observer.Commands[0], StringComparison.Ordinal);
        Assert.Equal(QueryTestSupport.RootAlias(generatedSql), QueryTestSupport.RootAlias(observer.Commands[0]));

        await using var competitor = fixture.CreateContext();
        await using var competitorTransaction = await competitor.Database.BeginTransactionAsync();
        var competitorBackend = await BackendIdAsync(competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction());
        Assert.NotEqual(holderBackend, competitorBackend);

        await ExecuteAsync(competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
            "UPDATE \"Rows\" SET \"Payload\" = 'changed' WHERE \"Id\" = 2");
        await ExecuteAsync(competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
            "UPDATE \"Peers\" SET \"Payload\" = 'changed' WHERE \"Id\" = 7");
        var locked = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            competitor.Database.GetDbConnection(),
            competitorTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE NOWAIT"));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, locked.SqlState);

        await competitorTransaction.RollbackAsync();
        await holderTransaction.CommitAsync();

        await using var afterRelease = fixture.CreateContext();
        await using var afterReleaseTransaction = await afterRelease.Database.BeginTransactionAsync();
        await ExecuteAsync(afterRelease.Database.GetDbConnection(), afterReleaseTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE NOWAIT");
        await afterReleaseTransaction.RollbackAsync();

        QueryTestSupport.WriteOutcome(output, "Q01", candidate, "supported-pass",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands[0])};selectedIds={selected.Id};backendIds={holderBackend},{competitorBackend};targetNowait={locked.SqlState};nonTargetRows=updated-before-conflict;competitorRollback=after-conflict;release=commit");
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task Q02_for_update_before_where_preserves_root_lock_scope(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "target" },
            new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "other" });

        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, candidate, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var query = context.Rows.ForUpdate().Where(row => row.Id == 2);
        var generatedSql = query.ToQueryString();
        var selected = await query.SingleAsync();

        Assert.Equal(2, selected.Id);
        Assert.Single(observer.Commands);
        Assert.Contains($"FOR UPDATE OF {QueryTestSupport.RootAlias(generatedSql)}", generatedSql, StringComparison.Ordinal);
        Assert.Contains($"FOR UPDATE OF {QueryTestSupport.RootAlias(observer.Commands[0])}", observer.Commands[0], StringComparison.Ordinal);
        Assert.Equal(QueryTestSupport.RootAlias(generatedSql), QueryTestSupport.RootAlias(observer.Commands[0]));

        await using var competitor = fixture.CreateContext();
        await using var otherTransaction = await competitor.Database.BeginTransactionAsync();
        await ExecuteAsync(competitor.Database.GetDbConnection(), otherTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE NOWAIT");
        var rejectedTarget = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            competitor.Database.GetDbConnection(), otherTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 2 FOR UPDATE NOWAIT"));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, rejectedTarget.SqlState);

        await otherTransaction.RollbackAsync();
        await transaction.RollbackAsync();
        await using var afterRollback = fixture.CreateContext();
        await using var afterRollbackTransaction = await afterRollback.Database.BeginTransactionAsync();
        await ExecuteAsync(afterRollback.Database.GetDbConnection(), afterRollbackTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 2 FOR UPDATE NOWAIT");
        await afterRollbackTransaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "Q02", candidate, "supported-pass",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands[0])};selectedIds={selected.Id};otherRowNowait=pass;targetNowait={rejectedTarget.SqlState};postRollbackNowait=pass;release=rollback");
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task Q03_as_no_tracking_for_share_is_observed_by_postgresql(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "target" });

        var observer = new SqlCommandObserver();
        await using var holder = QueryTestSupport.CreateContext(fixture, candidate, observer);
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        var query = holder.Rows.AsNoTracking().Where(row => row.Id == 1).ForShare();
        var generatedSql = query.ToQueryString();
        var selected = await query.SingleAsync();
        Assert.Equal(1, selected.Id);
        Assert.Single(observer.Commands);
        Assert.Contains($"FOR SHARE OF {QueryTestSupport.RootAlias(generatedSql)}", generatedSql, StringComparison.Ordinal);
        Assert.Contains($"FOR SHARE OF {QueryTestSupport.RootAlias(observer.Commands[0])}", observer.Commands[0], StringComparison.Ordinal);
        Assert.Equal(QueryTestSupport.RootAlias(generatedSql), QueryTestSupport.RootAlias(observer.Commands[0]));

        await using var competitor = fixture.CreateContext();
        await using var competitorTransaction = await competitor.Database.BeginTransactionAsync();
        await ExecuteAsync(competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 1 FOR SHARE NOWAIT");
        var updateConflict = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE NOWAIT"));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, updateConflict.SqlState);

        await competitorTransaction.RollbackAsync();
        await holderTransaction.CommitAsync();
        QueryTestSupport.WriteOutcome(output, "Q03", candidate, "supported-pass",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands[0])};selectedIds={selected.Id};shareCompatible=pass;updateSqlState={updateConflict.SqlState};release=commit");
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task Q04_order_by_take_locks_only_the_returned_row(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 1, Rank = 30, Payload = "third" },
            new ProbeRow { Id = 2, PeerId = 2, Rank = 10, Payload = "first" },
            new ProbeRow { Id = 3, PeerId = 3, Rank = 20, Payload = "second" });

        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, candidate, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var query = context.Rows.OrderBy(row => row.Rank).Take(1).ForUpdate();
        var generatedSql = query.ToQueryString();
        var selected = await query.SingleAsync();

        Assert.Equal(2, selected.Id);
        Assert.Single(observer.Commands);
        Assert.Contains($"FOR UPDATE OF {QueryTestSupport.RootAlias(generatedSql)}", generatedSql, StringComparison.Ordinal);
        var parameters = observer.Parameters.Single();
        var limit = Regex.Match(observer.Commands[0], @"\bLIMIT\s+(?<expression>[^\r\n]+)", RegexOptions.CultureInvariant);
        Assert.True(limit.Success, $"Expected a LIMIT expression in executed SQL: {observer.Commands[0]}");
        var takeParameter = Assert.Single(parameters, parameter => Equals(parameter.Value, 1));
        var takeParameterName = Regex.Escape(takeParameter.Name.TrimStart('@', '$'));
        Assert.Matches($@"(?<![A-Za-z0-9_])(?:@|\$){takeParameterName}(?![A-Za-z0-9_])", limit.Groups["expression"].Value);
        Assert.Contains($"FOR UPDATE OF {QueryTestSupport.RootAlias(observer.Commands[0])}", observer.Commands[0], StringComparison.Ordinal);
        Assert.Equal(QueryTestSupport.RootAlias(generatedSql), QueryTestSupport.RootAlias(observer.Commands[0]));

        await using var competitor = fixture.CreateContext();
        await using var otherTransaction = await competitor.Database.BeginTransactionAsync();
        await ExecuteAsync(competitor.Database.GetDbConnection(), otherTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE NOWAIT");
        await ExecuteAsync(competitor.Database.GetDbConnection(), otherTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 3 FOR UPDATE NOWAIT");
        var targetConflict = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            competitor.Database.GetDbConnection(), otherTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 2 FOR UPDATE NOWAIT"));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, targetConflict.SqlState);
        await otherTransaction.RollbackAsync();
        await transaction.RollbackAsync();

        QueryTestSupport.WriteOutcome(output, "Q04", candidate, "supported-pass",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands[0])};parameters={string.Join(',', parameters.Select(parameter => $"{parameter.Name}={parameter.Value}"))};selectedIds={selected.Id};nonTargetNowait=pass;targetNowait={targetConflict.SqlState};limitParameter={takeParameter.Name}:{takeParameter.Value};release=rollback");
    }

    private static async Task<int> BackendIdAsync(DbConnection connection, DbTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_backend_pid()";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<object?> ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
