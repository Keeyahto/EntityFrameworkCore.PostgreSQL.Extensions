using EntityFrameworkCore.PostgreSQL.Extensions;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Linq.Expressions;
using System.Text;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class UnsupportedShapeTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task Q05_q10_and_q12_unsupported_relational_shapes_reject_before_select()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "root" },
            new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "other" });

        await AssertRejectedAsync("Q05-inner-join", context =>
            (from row in context.Rows.ForUpdate()
             join peer in context.Peers on row.PeerId equals peer.Id
             select row).ToListAsync(), "query operator 'Join' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q06-left-join-nullable-side", context =>
            (from row in context.Rows.ForUpdate()
             join peer in context.Peers on row.PeerId equals peer.Id into peers
             from peer in peers.DefaultIfEmpty()
             select new { row, peer }).ToListAsync(), "query operator 'SelectMany' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q07-self-join", context =>
            (from left in context.Rows.ForUpdate()
             join right in context.Rows on left.PeerId equals right.PeerId
             select new { left, right }).ToListAsync(), "query operator 'Join' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q08-projection", context =>
            context.Rows.Where(row => row.Id == 1).ForUpdate()
                .Select(row => new { row.Id, row.Payload }).ToListAsync(), "query operator 'Select' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q09-include-split", context =>
            context.Rows.ForUpdate().Include("UnknownNavigation").AsSplitQuery().ToListAsync(), "query operator 'AsSplitQuery' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q09-split-only", context =>
            context.Rows.ForUpdate().AsSplitQuery().ToListAsync(), "query operator 'AsSplitQuery' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q10-nested-subquery", context =>
            context.Rows.Where(row => context.Rows.ForUpdate().Any(inner => inner.Id == row.Id)).ToListAsync(), "predicates may contain only direct mapped scalar equality and AND");

        await AssertRejectedAsync("Q04-offset-selection", context =>
            context.Rows.OrderBy(row => row.Rank).Skip(1).Take(1).ForUpdate().ToListAsync(), "query operator 'Skip' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q10-native-take-then-filter-pushdown", context =>
            context.Rows.OrderBy(row => row.Rank).Take(2).Where(row => row.Id == 1).ForUpdate().ToListAsync(),
            "the finalized SELECT must retain one exact mapped table occurrence and a stable lock scope");

        var includeRoot = new ProbeRow { Id = 11, PeerId = 7, Rank = 10, Payload = "include-root" };
        includeRoot.Children.Add(new ProbeChild { Id = 21, Payload = "included-child" });
        await QueryTestSupport.SeedAsync(fixture, includeRoot);
        await AssertRejectedAsync("Q09-valid-collection-include-split", context =>
            context.Rows.Include(row => row.Children).AsSplitQuery().ForUpdate().ToListAsync(), "query operator 'AsSplitQuery' is outside the admitted root lock grammar");

        await AssertRejectedAsync("Q12-union", context =>
            context.Rows.ForUpdate().Union(context.Rows).ToListAsync(), "query operator 'Union' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q12-except", context =>
            context.Rows.ForUpdate().Except(context.Rows).ToListAsync(), "query operator 'Except' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q12-intersect", context =>
            context.Rows.ForUpdate().Intersect(context.Rows).ToListAsync(), "query operator 'Intersect' is outside the admitted root lock grammar");
    }

    [Fact]
    public async Task Q11_distinct_group_and_scalar_aggregates_reject_before_select()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "root" });

        await AssertRejectedAsync("Q11-distinct", context =>
            context.Rows.ForUpdate().Distinct().ToListAsync(), "query operator 'Distinct' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q11-group-by", context =>
            context.Rows.ForUpdate().GroupBy(row => row.PeerId).Select(group => group.Count()).ToListAsync(), "query operator 'Select' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q11-count", context =>
            context.Rows.ForUpdate().CountAsync(), "query operator 'Count' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q11-sum", context =>
            context.Rows.ForUpdate().SumAsync(row => row.Rank), "query operator 'Sum' is outside the admitted root lock grammar");
    }

    [Fact]
    public async Task Q24_non_equality_predicates_and_predicate_subqueries_reject_before_select()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 40, PeerId = 7, Rank = 10, Payload = "predicate" },
            new ProbeRow { Id = 41, PeerId = 8, Rank = 20, Payload = "other" });

        await AssertRejectedAsync("Q24-or", context =>
            context.Rows.Where(row => row.Id == 40 || row.Id == 41).ForUpdate().ToListAsync(),
            "predicates may contain only direct mapped scalar equality and AND");
        await AssertRejectedAsync("Q24-inequality", context =>
            context.Rows.Where(row => row.Id != 40).ForUpdate().ToListAsync(),
            "predicates may contain only direct mapped scalar equality and AND");
        await AssertRejectedAsync("Q24-method-predicate", context =>
            context.Rows.Where(row => row.Payload.StartsWith("pred")).ForUpdate().ToListAsync(),
            "predicates may contain only direct mapped scalar equality and AND");
        var navigationObserver = new SqlCommandObserver();
        var navigationOptions = new DbContextOptionsBuilder<NavigationProbeContext>().UseProviderTestWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions())
            .AddInterceptors(navigationObserver)
            .Options;
        await using (var navigationContext = new NavigationProbeContext(navigationOptions))
        {
            await AssertMappedRejectedAsync("Q24-navigation-predicate", navigationContext,
                () => navigationContext.Rows.Where(row => row.Peer.Payload == "peer").ForUpdate().ToListAsync(),
                "equality must compare a direct root scalar member");
        }
        await AssertRejectedAsync("Q24-subquery-predicate", context =>
            context.Rows.Where(row => context.Peers.Any(peer => peer.Id == row.PeerId)).ForUpdate().ToListAsync(),
            "predicates may contain only direct mapped scalar equality and AND");
        await AssertRejectedAsync("Q24-any-terminal", context =>
            context.Rows.Where(row => row.Id == 40).ForUpdate().AnyAsync(),
            "query operator 'Any' is outside the admitted root lock grammar");
    }

    [Fact]
    public async Task Q25_identity_select_take_two_and_invalid_order_reject_before_select()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 42, PeerId = 7, Rank = 10, Payload = "shape" });

        await AssertRejectedAsync("Q25-identity-select", context =>
            context.Rows.Where(row => row.Id == 42).ForUpdate().Select(row => row).ToListAsync(),
            "operator 'Select' is outside the admitted root lock grammar");
        await AssertRejectedAsync("Q25-take-two", context =>
            context.Rows.OrderBy(row => row.Rank).Take(2).ForUpdate().ToListAsync(),
            "the exact Take(1) query parameter must have value 1 at execution");
        await AssertRejectedAsync("Q25-invalid-order", context =>
            context.Rows.OrderBy(row => row.Payload.Length).Take(1).ForUpdate().ToListAsync(),
            "ordering keys must be direct mapped scalar members");
    }

    [Fact]
    public async Task Q26_named_and_anonymous_global_filters_reject_before_select()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 43, PeerId = 7, Rank = 10, Payload = "filtered" });
        var anonymousObserver = new SqlCommandObserver();
        var anonymousOptions = new DbContextOptionsBuilder<FilteredProbeContext>().UseProviderTestWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions())
            .AddInterceptors(anonymousObserver)
            .Options;
        await using (var context = new FilteredProbeContext(anonymousOptions))
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Rows
            .Where(row => row.Id == 43).ForUpdate().ToListAsync());
            AssertRejectedForReason(exception, "one unfiltered, non-inherited");
            Assert.Equal(0, anonymousObserver.Count);
            QueryTestSupport.WriteOutcome(output, "Q26-anonymous-filter", "explicit-reject-as-designed",
                $"reason={exception.Message};issuedCommands={anonymousObserver.Count}");
            await transaction.RollbackAsync();
        }

        var namedObserver = new SqlCommandObserver();
        var namedOptions = new DbContextOptionsBuilder<NamedFilteredProbeContext>().UseProviderTestWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions())
            .AddInterceptors(namedObserver)
            .Options;
        await using (var context = new NamedFilteredProbeContext(namedOptions))
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Rows
                .Where(row => row.Id == 43).ForUpdate().ToListAsync());
            AssertRejectedForReason(exception, "one unfiltered, non-inherited");
            Assert.Equal(0, namedObserver.Count);
            QueryTestSupport.WriteOutcome(output, "Q26-named-filter", "explicit-reject-as-designed",
                $"reason={exception.Message};issuedCommands={namedObserver.Count}");
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Q27_inheritance_view_shared_table_and_complex_mapping_reject_before_select()
    {
        var observer = new SqlCommandObserver();
        var options = new DbContextOptionsBuilder<UnsupportedMappingContext>().UseProviderTestWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions())
            .AddInterceptors(observer)
            .Options;
        await using var context = new UnsupportedMappingContext(options);

        await AssertMappedRejectedAsync("Q27-inheritance", context,
            () => context.InheritedRows.ForUpdate().ToListAsync(),
            "the entity mapping must be one unfiltered, non-inherited");
        await AssertMappedRejectedAsync("Q27-view", context,
            () => context.ViewRows.ForUpdate().ToListAsync(),
            "the entity mapping must be one unfiltered, non-inherited");
        await AssertMappedRejectedAsync("Q27-shared-table", context,
            () => context.SharedRows.ForUpdate().ToListAsync(),
            "shared-table entity mappings are outside");
        await AssertMappedRejectedAsync("Q27-complex-property", context,
            () => context.ComplexRows.ForUpdate().ToListAsync(),
            "the entity mapping must be one unfiltered, non-inherited");
        Assert.Equal(0, observer.Count);
    }

    [Fact]
    public async Task Q28_raw_sql_roots_and_auto_includes_reject_before_select()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 44, PeerId = 7, Rank = 10, Payload = "raw-root" });
        await AssertRejectedAsync("Q28-raw-root", context =>
            context.Rows.FromSqlRaw("SELECT * FROM \"Rows\"").ForUpdate().ToListAsync(),
            "query must be a direct mapped entity-root chain");

        var observer = new SqlCommandObserver();
        var options = new DbContextOptionsBuilder<AutoIncludeNavigationContext>().UseProviderTestWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions())
            .AddInterceptors(observer)
            .Options;
        await using var context = new AutoIncludeNavigationContext(options);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Rows
            .Where(row => row.Id == 44).ForUpdate().ToListAsync());
        AssertRejectedForReason(exception, "eager navigations or complex properties");
        Assert.Equal(0, observer.Count);
        QueryTestSupport.WriteOutcome(output, "Q28-auto-include", "explicit-reject-as-designed",
            $"reason={exception.Message};issuedCommands={observer.Count}");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Q13_user_tags_are_preserved_and_reserved_spoofs_are_rejected()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "tagged" });

        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
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
        QueryTestSupport.WriteOutcome(output, "Q13-user-tag", "supported-pass",
            $"generated={OneLine(generatedSql)};executed={OneLine(observer.Commands.Single())};ids={result.Id};userTag=preserved;targetNowait={taggedTargetConflict.SqlState}");

        var validPairedSpoofTag = BuildValidPairedSpoofTag();
        var spoofObserver = new SqlCommandObserver();
        await using var spoofContext = QueryTestSupport.CreateContext(fixture, spoofObserver);
        var spoofedWithoutMarker = spoofContext.Rows.TagWith(validPairedSpoofTag).Where(row => row.Id == 1);
        await AssertRejectedAsync("Q13-reserved-spoof-no-marker", spoofContext, spoofObserver,
            () => spoofedWithoutMarker.ToListAsync(), "a user query tag spoofs the reserved lock protocol");

        const string spoofTag = "user comment\n__pgext_lock_v1:0:0:0\nstill user comment";
        var markedSpoofObserver = new SqlCommandObserver();
        await using var markedSpoofContext = QueryTestSupport.CreateContext(fixture, markedSpoofObserver);
        var spoofedWithMarker = markedSpoofContext.Rows.TagWith(spoofTag).Where(row => row.Id == 1).ForUpdate();
        await AssertRejectedAsync("Q13-reserved-spoof-with-marker", markedSpoofContext, markedSpoofObserver,
            () => spoofedWithMarker.ToListAsync(), "user query tag spoofs the reserved lock protocol");
    }

    [Fact]
    public async Task Q14_repeated_and_conflicting_requests_have_explicit_outcomes()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "repeat" });

        var identicalObserver = new SqlCommandObserver();
        await using var identicalContext = QueryTestSupport.CreateContext(fixture, identicalObserver);
        var identical = identicalContext.Rows.Where(row => row.Id == 1).ForUpdate().ForUpdate();
        await AssertRejectedAsync("Q14-identical", identicalContext, identicalObserver,
            () => identical.ToListAsync(), "only one row-lock request");

        var conflictingObserver = new SqlCommandObserver();
        await using var conflictingContext = QueryTestSupport.CreateContext(fixture, conflictingObserver);
        var conflicting = conflictingContext.Rows.Where(row => row.Id == 1).ForUpdate().ForShare();
        await AssertRejectedAsync("Q14-conflicting", conflictingContext, conflictingObserver,
            () => conflicting.ToListAsync(), "only one row-lock request");
    }

    [Fact]
    public async Task Q16_dynamic_lock_options_reject_as_nonstructural()
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "dynamic" });

        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
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
        QueryTestSupport.WriteOutcome(output, "Q16", "explicit-reject-as-designed",
            $"reason={exception.Message};issuedCommands={observer.Count}");
        await transaction.RollbackAsync();
    }

    private async Task AssertRejectedAsync(
        string queryId,
        Func<ProbeContext, Task> execute,
        string expectedReason)
    {
        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
        await AssertRejectedAsync(queryId, context, observer, () => execute(context), expectedReason);
    }

    private async Task AssertMappedRejectedAsync(
        string queryId,
        DbContext context,
        Func<Task> execute,
        string expectedReason)
    {
        await using var transaction = await context.Database.BeginTransactionAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(execute);
        AssertRejectedForReason(exception, expectedReason);
        QueryTestSupport.WriteOutcome(output, queryId, "explicit-reject-as-designed",
            $"reason={exception.Message};issuedCommands=0");
        await transaction.RollbackAsync();
    }

    private async Task AssertRejectedAsync(
        string queryId,
        ProbeContext context,
        SqlCommandObserver observer,
        Func<Task> execute,
        string expectedReason)
    {
        await using var transaction = await context.Database.BeginTransactionAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(execute);
        AssertRejectedForReason(exception, expectedReason);
        Assert.Equal(0, observer.Count);
        QueryTestSupport.WriteOutcome(output, queryId, "explicit-reject-as-designed",
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

    private static string BuildValidPairedSpoofTag()
    {
        static string EncodeComponent(string value)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        var encodedEntity = EncodeComponent(nameof(ProbeRow));
        var encodedTable = EncodeComponent("Rows");
        var payload = $"0:0:0:-:{encodedEntity}:{encodedTable}:-";
        return $"padding __pgext_lock_v1:{payload}\npadding __pgext_lock_fulfilled_v1:{payload}*/";
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
