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

[Fact]
    public async Task Q01_where_then_for_update_locks_only_the_selected_root_row()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new[]
            {
                new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "target" },
                new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "other" }
            },
            new[] { new PeerRow { Id = 7, Payload = "join-target" } });

        var observer = new SqlCommandObserver();
        await using var holder = QueryTestSupport.CreateContext(fixture, observer);
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

        QueryTestSupport.WriteOutcome(output, "Q01", "supported-pass",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands[0])};selectedIds={selected.Id};backendIds={holderBackend},{competitorBackend};targetNowait={locked.SqlState};nonTargetRows=updated-before-conflict;competitorRollback=after-conflict;release=commit");
    }

    [Fact]
    public async Task Q02_for_update_before_where_preserves_root_lock_scope()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "target" },
            new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "other" });

        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
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
        QueryTestSupport.WriteOutcome(output, "Q02", "supported-pass",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands[0])};selectedIds={selected.Id};otherRowNowait=pass;targetNowait={rejectedTarget.SqlState};postRollbackNowait=pass;release=rollback");
    }

    [Fact]
    public async Task Q03_as_no_tracking_for_share_is_observed_by_postgresql()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "target" });

        var observer = new SqlCommandObserver();
        await using var holder = QueryTestSupport.CreateContext(fixture, observer);
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
        QueryTestSupport.WriteOutcome(output, "Q03", "supported-pass",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands[0])};selectedIds={selected.Id};shareCompatible=pass;updateSqlState={updateConflict.SqlState};release=commit");
    }

    [Fact]
    public async Task Q04_order_by_take_locks_only_the_returned_row()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 1, Rank = 30, Payload = "third" },
            new ProbeRow { Id = 2, PeerId = 2, Rank = 10, Payload = "first" },
            new ProbeRow { Id = 3, PeerId = 3, Rank = 20, Payload = "second" });

        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
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

        QueryTestSupport.WriteOutcome(output, "Q04", "supported-pass",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands[0])};parameters={string.Join(',', parameters.Select(parameter => $"{parameter.Name}={parameter.Value}"))};selectedIds={selected.Id};nonTargetNowait=pass;targetNowait={targetConflict.SqlState};limitParameter={takeParameter.Name}:{takeParameter.Value};release=rollback");
    }

    [Fact]
    public async Task Q19_scalar_natural_composite_text_nullable_and_converted_predicates_execute_with_locks()
    {
        var row = new ProbeRow
        {
            Id = 19,
            PeerId = 7,
            Rank = 19,
            Payload = "natural text key",
            NaturalCode = "natural-19",
            CompositeLeft = "tenant-19",
            CompositeRight = "item-19",
            NullableKey = 19,
            ConvertedKey = new ConvertedProbeKey("converted-19")
        };
        await QueryTestSupport.SeedAsync(fixture, row);

        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var capturedNaturalCode = "natural-19";
        var natural = await context.Rows.Where(value => value.NaturalCode == capturedNaturalCode).ForUpdate().SingleAsync();
        var composite = await context.Rows
            .Where(value => value.CompositeLeft == "tenant-19" && value.CompositeRight == "item-19")
            .ForShare()
            .SingleAsync();
        var text = await context.Rows.Where(value => value.Payload == "natural text key").ForUpdate().SingleAsync();
        int? nullableKey = 19;
        var nullable = await context.Rows.Where(value => value.NullableKey == nullableKey).ForShare().SingleAsync();
        var convertedKey = new ConvertedProbeKey("converted-19");
        var converted = await context.Rows.Where(value => value.ConvertedKey == convertedKey).ForUpdate().SingleAsync();

        Assert.Equal(new[] { 19, 19, 19, 19, 19 }, new[] { natural.Id, composite.Id, text.Id, nullable.Id, converted.Id });
        Assert.Equal(5, observer.Count);
        Assert.All(observer.Commands, command => Assert.Contains("FOR ", command, StringComparison.Ordinal));
        Assert.Contains(observer.Parameters.SelectMany(parameters => parameters), parameter => Equals(parameter.Value, capturedNaturalCode));
        Assert.Contains(observer.Parameters.SelectMany(parameters => parameters), parameter => Equals(parameter.Value, "converted-19"));
        await transaction.RollbackAsync();

        QueryTestSupport.WriteOutcome(output, "Q19", "supported-pass",
            $"ids={natural.Id},{composite.Id},{text.Id},{nullable.Id},{converted.Id};queries={observer.Count};allLocked=pass;capturedAndConvertedParameters=observed");
    }

    [Fact]
    public async Task Q20_sync_and_async_entity_enumeration_execute_admitted_roots()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 20, PeerId = 7, Rank = 20, Payload = "sync" },
            new ProbeRow { Id = 21, PeerId = 8, Rank = 21, Payload = "async" });
        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();

        var syncIds = context.Rows.Where(row => row.Id == 20).ForUpdate().AsEnumerable().Select(row => row.Id).ToArray();
        var asyncIds = new List<int>();
        await foreach (var row in context.Rows.Where(value => value.Id == 21).ForShare().AsAsyncEnumerable())
        {
            asyncIds.Add(row.Id);
        }

        Assert.Equal(new[] { 20 }, syncIds);
        Assert.Equal(new[] { 21 }, asyncIds);
        Assert.Equal(2, observer.Count);
        Assert.All(observer.Commands, command => Assert.Contains("FOR ", command, StringComparison.Ordinal));
        await transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "Q20", "supported-pass",
            $"syncIds={string.Join(',', syncIds)};asyncIds={string.Join(',', asyncIds)};commands={observer.Count}");
    }

    [Fact]
    public async Task Q21_order_then_by_take_one_locks_only_the_first_row()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 22, PeerId = 1, Rank = 5, Payload = "tie-a" },
            new ProbeRow { Id = 23, PeerId = 1, Rank = 5, Payload = "tie-b" },
            new ProbeRow { Id = 24, PeerId = 2, Rank = 6, Payload = "later" });
        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var query = context.Rows.OrderBy(value => value.Rank).ThenBy(value => value.Id).Take(1).ForUpdate();
        var row = await query.SingleAsync();
        var sql = Assert.Single(observer.Commands);
        Assert.Equal(22, row.Id);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR UPDATE OF", sql, StringComparison.Ordinal);

        await using var competitor = fixture.CreateContext();
        await using var competitorTransaction = await competitor.Database.BeginTransactionAsync();
        await QueryTestSupport.ExecuteScalarAsync(competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 23 FOR UPDATE NOWAIT");
        var lockedTarget = await Assert.ThrowsAsync<PostgresException>(() => QueryTestSupport.ExecuteScalarAsync(
            competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 22 FOR UPDATE NOWAIT"));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, lockedTarget.SqlState);
        await competitorTransaction.RollbackAsync();
        await transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "Q21", "supported-pass",
            $"sql={OneLine(sql)};selected={row.Id};otherRowNowait=pass;targetNowait={lockedTarget.SqlState};release=rollback");
    }

    [Fact]
    public async Task Q22_terminal_predicate_overloads_preserve_empty_and_multiple_result_semantics()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 25, PeerId = 7, Rank = 1, Payload = "terminal-one" },
            new ProbeRow { Id = 26, PeerId = 7, Rank = 2, Payload = "terminal-two" });
        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();

        var single = await context.Rows.ForUpdate().SingleAsync(value => value.Id == 25);
        var singleOrDefault = await context.Rows.ForShare().SingleOrDefaultAsync(value => value.Id == 999);
        var first = await context.Rows.ForUpdate().FirstAsync(value => value.Id == 26);
        var firstOrDefault = await context.Rows.ForShare().FirstOrDefaultAsync(value => value.Id == 999);
        var syncFirst = context.Rows.ForUpdate().First(value => value.Id == 25);
        var syncSingleOrDefault = context.Rows.ForShare().SingleOrDefault(value => value.Id == 999);
        var multiplicity = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Rows.ForUpdate().SingleAsync(value => value.PeerId == 7));

        Assert.Equal(25, single.Id);
        Assert.Null(singleOrDefault);
        Assert.Equal(26, first.Id);
        Assert.Null(firstOrDefault);
        Assert.Equal(25, syncFirst.Id);
        Assert.Null(syncSingleOrDefault);
        Assert.Contains("more than one", multiplicity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, observer.Count);
        Assert.All(observer.Commands, command => Assert.Contains("FOR ", command, StringComparison.Ordinal));
        await transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "Q22", "supported-pass",
            $"ids={single.Id},{first.Id},{syncFirst.Id};emptySingleOrDefault=null;emptyFirstOrDefault=null;multiple=provider-semantic-error;commands={observer.Count}");
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
