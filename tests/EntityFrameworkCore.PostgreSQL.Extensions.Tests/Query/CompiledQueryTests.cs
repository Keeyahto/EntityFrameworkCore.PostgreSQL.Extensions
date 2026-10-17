using EntityFrameworkCore.PostgreSQL.Extensions;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class CompiledQueryTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task Q15_sync_and_async_compiled_queries_keep_lock_and_parameterized_predicate()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "locked" },
            new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "ordinary" });

        var syncObserver = new SqlCommandObserver();
        await using (var context = QueryTestSupport.CreateContext(fixture, syncObserver))
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Func<ProbeContext, int, ProbeRow> compiled = EF.CompileQuery(
                (ProbeContext db, int id) => db.Rows.Where(row => row.Id == id).ForUpdate().Single());
            var selected = compiled(context, 1);
            Assert.Equal(1, selected.Id);
            var lockedSql = Assert.Single(syncObserver.Commands);
            Assert.Contains("FOR UPDATE OF", lockedSql, StringComparison.Ordinal);
            var targetState = await AssertLockScopeAsync(1, 2);

            Func<ProbeContext, int, ProbeRow> ordinary = EF.CompileQuery(
                (ProbeContext db, int id) => db.Rows.Where(row => row.Id == id).Single());
            var unlocked = ordinary(context, 2);
            Assert.Equal(2, unlocked.Id);
            var ordinarySql = syncObserver.Commands.Last();
            Assert.DoesNotContain("FOR UPDATE", ordinarySql, StringComparison.Ordinal);
            QueryTestSupport.WriteOutcome(output, "Q15-sync", "supported-pass",
                $"lockedSql={OneLine(lockedSql)};unmarkedSql={OneLine(ordinarySql)};ids={selected.Id},{unlocked.Id};targetNowait={targetState};nonTargetNowait=pass;commandCount={syncObserver.Count}");
        }

        var asyncObserver = new SqlCommandObserver();
        await using (var context = QueryTestSupport.CreateContext(fixture, asyncObserver))
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Func<ProbeContext, int, IAsyncEnumerable<ProbeRow>> compiled = EF.CompileAsyncQuery(
                (ProbeContext db, int id) => db.Rows.Where(row => row.Id == id).ForShare());
            var selectedIds = new List<int>();
            await foreach (var row in compiled(context, 1))
            {
                selectedIds.Add(row.Id);
            }

            Assert.Equal(new[] { 1 }, selectedIds);
            var lockedSql = Assert.Single(asyncObserver.Commands);
            Assert.Contains("FOR SHARE OF", lockedSql, StringComparison.Ordinal);
            var targetState = await AssertLockScopeAsync(1, 2);
            QueryTestSupport.WriteOutcome(output, "Q15-async", "supported-pass",
                $"lockedSql={OneLine(lockedSql)};ids={string.Join(',', selectedIds)};targetNowait={targetState};nonTargetNowait=pass;commandCount={asyncObserver.Count}");
        }
    }

    [Fact]
    public async Task Q23_cached_take_one_accepts_one_and_rejects_take_two_before_select()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 31, PeerId = 1, Rank = 1, Payload = "first" },
            new ProbeRow { Id = 32, PeerId = 1, Rank = 2, Payload = "second" });
        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();
        Func<ProbeContext, int, IEnumerable<ProbeRow>> compiled = EF.CompileQuery(
            (ProbeContext db, int take) => db.Rows.OrderBy(row => row.Rank).Take(take).ForUpdate());

        var first = compiled(context, 1).ToArray();
        var firstSql = Assert.Single(observer.Commands);
        Assert.Equal(new[] { 31 }, first.Select(row => row.Id));
        Assert.Contains("LIMIT", firstSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR UPDATE OF", firstSql, StringComparison.Ordinal);
        var takeParameter = Assert.Single(observer.Parameters.Single(), parameter => Equals(parameter.Value, 1));
        var limit = Regex.Match(firstSql, @"\bLIMIT\s+(?<expression>[^\r\n]+)", RegexOptions.CultureInvariant);
        Assert.True(limit.Success, $"Expected a LIMIT expression in the executed SQL: {firstSql}");
        var parameterName = Regex.Escape(takeParameter.Name.TrimStart('@', '$'));
        Assert.Matches($@"(?<![A-Za-z0-9_])(?:@|\$){parameterName}(?![A-Za-z0-9_])", limit.Groups["expression"].Value);

        var exception = Assert.Throws<InvalidOperationException>(() => compiled(context, 2).ToArray());
        Assert.StartsWith("The PostgreSQL row-lock request cannot be fulfilled:", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, observer.Count);
        await transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "Q23", "explicit-reject-as-designed",
            $"acceptedIds={string.Join(',', first.Select(row => row.Id))};firstSql={OneLine(firstSql)};takeOneParameter=observed;takeTwoReason={exception.Message};commands={observer.Count}");
    }

    private async Task<string> AssertLockScopeAsync(int targetId, int nonTargetId)
    {
        await using var competitor = fixture.CreateContext();
        await using var competitorTransaction = await competitor.Database.BeginTransactionAsync();
        await QueryTestSupport.ExecuteScalarAsync(competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
            $"SELECT 1 FROM \"Rows\" WHERE \"Id\" = {nonTargetId} FOR UPDATE NOWAIT");
        var conflict = await Assert.ThrowsAsync<PostgresException>(() => QueryTestSupport.ExecuteScalarAsync(
            competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
            $"SELECT 1 FROM \"Rows\" WHERE \"Id\" = {targetId} FOR UPDATE NOWAIT"));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, conflict.SqlState);
        await competitorTransaction.RollbackAsync();
        return conflict.SqlState;
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
