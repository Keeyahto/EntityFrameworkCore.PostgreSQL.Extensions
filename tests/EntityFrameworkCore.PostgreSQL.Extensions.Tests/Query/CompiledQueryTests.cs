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
public sealed class CompiledQueryTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Q15_sync_and_async_compiled_queries_keep_lock_or_reject_before_select(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "locked" },
            new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "ordinary" });

        var syncObserver = new SqlCommandObserver();
        await using (var context = QueryTestSupport.CreateContext(fixture, candidate, syncObserver))
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            try
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
                QueryTestSupport.WriteOutcome(output, "Q15-sync", candidate, "supported-pass",
                    $"lockedSql={OneLine(lockedSql)};unmarkedSql={OneLine(ordinarySql)};ids={selected.Id},{unlocked.Id};targetNowait={targetState};nonTargetNowait=pass;commandCount={syncObserver.Count}");
            }
            catch (InvalidOperationException exception) when (IsExplicitLockRejection(exception))
            {
                Assert.Equal(0, syncObserver.Count);
                QueryTestSupport.WriteOutcome(output, "Q15-sync", candidate, "explicit-reject-as-designed",
                    $"reason={exception.Message};issuedCommands={syncObserver.Count}");
            }
        }

        var asyncObserver = new SqlCommandObserver();
        await using (var context = QueryTestSupport.CreateContext(fixture, candidate, asyncObserver))
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            try
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
                QueryTestSupport.WriteOutcome(output, "Q15-async", candidate, "supported-pass",
                    $"lockedSql={OneLine(lockedSql)};ids={string.Join(',', selectedIds)};targetNowait={targetState};nonTargetNowait=pass;commandCount={asyncObserver.Count}");
            }
            catch (InvalidOperationException exception) when (IsExplicitLockRejection(exception))
            {
                Assert.Equal(0, asyncObserver.Count);
                QueryTestSupport.WriteOutcome(output, "Q15-async", candidate, "explicit-reject-as-designed",
                    $"reason={exception.Message};issuedCommands={asyncObserver.Count}");
            }
        }
    }

    private static bool IsExplicitLockRejection(InvalidOperationException exception)
        => exception.Message.StartsWith("The PostgreSQL row-lock request cannot be fulfilled:", StringComparison.Ordinal);

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
