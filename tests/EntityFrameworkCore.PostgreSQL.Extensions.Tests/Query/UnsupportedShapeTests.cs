using EntityFrameworkCore.PostgreSQL.Extensions;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Linq.Expressions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class UnsupportedShapeTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    public static TheoryData<PocCarrier> Candidates => AdmittedRootShapeTests.Candidates;

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task Q05_q10_and_q12_unsupported_relational_shapes_reject_before_select(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "root" },
            new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "other" });

        await AssertRejectedAsync("Q05-inner-join", candidate, context =>
            (from row in context.Rows.ForUpdate()
             join peer in context.Peers on row.PeerId equals peer.Id
             select row).ToListAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q06-left-join-nullable-side", candidate, context =>
            (from row in context.Rows.ForUpdate()
             join peer in context.Peers on row.PeerId equals peer.Id into peers
             from peer in peers.DefaultIfEmpty()
             select new { row, peer }).ToListAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q07-self-join", candidate, context =>
            (from left in context.Rows.ForUpdate()
             join right in context.Rows on left.PeerId equals right.PeerId
             select new { left, right }).ToListAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q08-projection", candidate, context =>
            context.Rows.Where(row => row.Id == 1).ForUpdate()
                .Select(row => new { row.Id, row.Payload }).ToListAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q09-include-split", candidate, context =>
            context.Rows.ForUpdate().Include("UnknownNavigation").AsSplitQuery().ToListAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q09-split-only", candidate, context =>
            context.Rows.ForUpdate().AsSplitQuery().ToListAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q10-nested-subquery", candidate, context =>
            context.Rows.Where(row => context.Rows.ForUpdate().Any(inner => inner.Id == row.Id)).ToListAsync(), "projection, join, or subquery argument");

        await AssertRejectedAsync("Q04-offset-selection", candidate, context =>
            context.Rows.OrderBy(row => row.Rank).Skip(1).Take(1).ForUpdate().ToListAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q10-native-take-then-filter-pushdown", candidate, context =>
            context.Rows.OrderBy(row => row.Rank).Take(2).Where(row => row.Id == 1).ForUpdate().ToListAsync(),
            candidate == PocCarrier.ExpressionTag
                ? "final SELECT no longer identifies one simple root table occurrence"
                : "one unambiguous mapped root entity table");

        var includeRoot = new ProbeRow { Id = 11, PeerId = 7, Rank = 10, Payload = "include-root" };
        includeRoot.Children.Add(new ProbeChild { Id = 21, Payload = "included-child" });
        await QueryTestSupport.SeedAsync(fixture, includeRoot);
        await AssertRejectedAsync("Q09-valid-collection-include-split", candidate, context =>
            context.Rows.Include(row => row.Children).AsSplitQuery().ForUpdate().ToListAsync(), "outside the admitted root lock scope");

        await AssertRejectedAsync("Q12-union", candidate, context =>
            context.Rows.ForUpdate().Union(context.Rows).ToListAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q12-except", candidate, context =>
            context.Rows.ForUpdate().Except(context.Rows).ToListAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q12-intersect", candidate, context =>
            context.Rows.ForUpdate().Intersect(context.Rows).ToListAsync(), "outside the admitted root lock scope");
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task Q11_distinct_group_and_scalar_aggregates_reject_before_select(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "root" });

        await AssertRejectedAsync("Q11-distinct", candidate, context =>
            context.Rows.ForUpdate().Distinct().ToListAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q11-group-by", candidate, context =>
            context.Rows.ForUpdate().GroupBy(row => row.PeerId).Select(group => group.Count()).ToListAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q11-count", candidate, context =>
            context.Rows.ForUpdate().CountAsync(), "outside the admitted root lock scope");
        await AssertRejectedAsync("Q11-sum", candidate, context =>
            context.Rows.ForUpdate().SumAsync(row => row.Rank), "outside the admitted root lock scope");
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task Q13_user_tags_are_preserved_and_reserved_spoofs_are_rejected(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "tagged" });

        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, candidate, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var taggedQuery = context.Rows.TagWith("consumer-query-tag").Where(row => row.Id == 1).ForUpdate();
        var generatedSql = taggedQuery.ToQueryString();
        var result = await taggedQuery.SingleAsync();
        Assert.Equal(1, result.Id);
        Assert.Contains("consumer-query-tag", generatedSql, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF", observer.Commands.Single(), StringComparison.Ordinal);
        await using var competitor = fixture.CreateContext();
        await using var competitorTransaction = await competitor.Database.BeginTransactionAsync();
        var taggedTargetConflict = await Assert.ThrowsAsync<PostgresException>(() => QueryTestSupport.ExecuteScalarAsync(
            competitor.Database.GetDbConnection(), competitorTransaction.GetDbTransaction(),
            "SELECT 1 FROM \"Rows\" WHERE \"Id\" = 1 FOR UPDATE NOWAIT"));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, taggedTargetConflict.SqlState);
        await competitorTransaction.RollbackAsync();
        await transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "Q13-user-tag", candidate, "supported-pass",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands.Single())};ids={result.Id};userTag=preserved;targetNowait={taggedTargetConflict.SqlState}");

        const string spoofTag = "user comment\n__pgext_lock_v1:0:0:0\nstill user comment";
        var spoofObserver = new SqlCommandObserver();
        await using var spoofContext = QueryTestSupport.CreateContext(fixture, candidate, spoofObserver);
        var spoofedWithoutMarker = spoofContext.Rows.TagWith(spoofTag).Where(row => row.Id == 1);
        await AssertRejectedAsync("Q13-reserved-spoof-no-marker", candidate, spoofContext, spoofObserver,
            () => spoofedWithoutMarker.ToListAsync(), "user query tag spoofs the reserved lock protocol");

        var markedSpoofObserver = new SqlCommandObserver();
        await using var markedSpoofContext = QueryTestSupport.CreateContext(fixture, candidate, markedSpoofObserver);
        var spoofedWithMarker = markedSpoofContext.Rows.TagWith(spoofTag).Where(row => row.Id == 1).ForUpdate();
        await AssertRejectedAsync("Q13-reserved-spoof-with-marker", candidate, markedSpoofContext, markedSpoofObserver,
            () => spoofedWithMarker.ToListAsync(), "user query tag spoofs the reserved lock protocol");
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task Q14_repeated_and_conflicting_requests_have_explicit_outcomes(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "repeat" });

        var identicalObserver = new SqlCommandObserver();
        await using var identicalContext = QueryTestSupport.CreateContext(fixture, candidate, identicalObserver);
        var identical = identicalContext.Rows.Where(row => row.Id == 1).ForUpdate().ForUpdate();
        await AssertRejectedAsync("Q14-identical", candidate, identicalContext, identicalObserver,
            () => identical.ToListAsync(), "only one row-lock request");

        var conflictingObserver = new SqlCommandObserver();
        await using var conflictingContext = QueryTestSupport.CreateContext(fixture, candidate, conflictingObserver);
        var conflicting = conflictingContext.Rows.Where(row => row.Id == 1).ForUpdate().ForShare();
        await AssertRejectedAsync("Q14-conflicting", candidate, conflictingContext, conflictingObserver,
            () => conflicting.ToListAsync(), "only one row-lock request");
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task Q16_dynamic_lock_options_reject_as_nonstructural(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "dynamic" });

        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, candidate, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var queryContext = Expression.Parameter(typeof(ProbeContext), "context");
            var strength = Expression.Parameter(typeof(LockStrength), "strength");
            var wait = Expression.Parameter(typeof(LockWait), "wait");
            var rows = Expression.Property(queryContext, nameof(ProbeContext.Rows));
            var marker = Expression.Call(
                typeof(RowLockExtensions).GetMethods()
                    .Single(method => method.Name == nameof(RowLockExtensions.WithRowLock))
                    .MakeGenericMethod(typeof(ProbeRow)),
                rows,
                strength,
                wait);
            var lambda = Expression.Lambda<Func<ProbeContext, LockStrength, LockWait, IEnumerable<ProbeRow>>>(
                Expression.Convert(marker, typeof(IEnumerable<ProbeRow>)),
                queryContext,
                strength,
                wait);
            var compiled = EF.CompileQuery(lambda);
            _ = compiled(context, LockStrength.Update, LockWait.NoWait).ToList();
        });

        AssertRejectedForReason(exception, "lock strength and wait options must be compile-time constants");
        Assert.Equal(0, observer.Count);
        QueryTestSupport.WriteOutcome(output, "Q16", candidate, "explicit-reject-as-designed",
            $"reason={exception.Message};issuedCommands={observer.Count}");
        await transaction.RollbackAsync();
    }

    private async Task AssertRejectedAsync(
        string queryId,
        PocCarrier candidate,
        Func<ProbeContext, Task> execute,
        string expectedReason)
    {
        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, candidate, observer);
        await AssertRejectedAsync(queryId, candidate, context, observer, () => execute(context), expectedReason);
    }

    private async Task AssertRejectedAsync(
        string queryId,
        PocCarrier candidate,
        ProbeContext context,
        SqlCommandObserver observer,
        Func<Task> execute,
        string expectedReason)
    {
        await using var transaction = await context.Database.BeginTransactionAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(execute);
        AssertRejectedForReason(exception, expectedReason);
        Assert.Equal(0, observer.Count);
        QueryTestSupport.WriteOutcome(output, queryId, candidate, "explicit-reject-as-designed",
            $"reason={exception.Message};issuedCommands={observer.Count}");
        await transaction.RollbackAsync();
    }

    private static void AssertRejectedForReason(InvalidOperationException exception, string expectedReason)
    {
        Assert.StartsWith("The PostgreSQL row-lock request cannot be fulfilled:", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedReason, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("current borrowed EF transaction", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fulfillment receipt", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
