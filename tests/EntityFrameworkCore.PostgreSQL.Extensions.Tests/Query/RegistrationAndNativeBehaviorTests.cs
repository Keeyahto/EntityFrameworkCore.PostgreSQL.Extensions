using EntityFrameworkCore.PostgreSQL.Extensions;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class RegistrationAndNativeBehaviorTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Q17_missing_registration_and_generator_overwrite_fail_before_unlocked_select(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "registered" });

        var missingObserver = new SqlCommandObserver();
        await using (var missing = QueryTestSupport.CreateContext(fixture, null, missingObserver))
        {
            var missingQuery = missing.Rows.Where(row => row.Id == 1).ForUpdate();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => missingQuery.ToListAsync());
            Assert.Contains(nameof(RowLockExtensions.ForUpdate), exception.Message, StringComparison.Ordinal);
            Assert.Contains("could not be translated", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, missingObserver.Count);
            QueryTestSupport.WriteOutcome(output, "Q17-no-registration", candidate, "explicit-reject-as-designed",
                $"reason={exception.Message};issuedCommands={missingObserver.Count}");
        }

        var replacedObserver = new SqlCommandObserver();
        var replacedOptions = BuildOptions(candidate, replacedObserver, replaceGenerator: true);
        await using var replaced = new ProbeContext(replacedOptions);
        await using var transaction = await replaced.Database.BeginTransactionAsync();
        var replacedQuery = replaced.Rows.Where(row => row.Id == 1).ForUpdate();
        var replacementFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => replacedQuery.ToListAsync());
        Assert.StartsWith("The PostgreSQL row-lock request cannot be fulfilled:", replacementFailure.Message, StringComparison.Ordinal);
        Assert.Equal(0, replacedObserver.Count);
        QueryTestSupport.WriteOutcome(output, "Q17-generator-overwritten", candidate, "explicit-reject-as-designed",
            $"reason={replacementFailure.Message};issuedCommands={replacedObserver.Count}");
        await transaction.RollbackAsync();
    }

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Q17_wrong_provider_rejects_marker_without_issuing_select(PocCarrier candidate)
    {
        var observer = new SqlCommandObserver();
        var options = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings()
            .UseSqlite("Data Source=:memory:")
            .AddInterceptors(observer)
            .Options;
        await using var context = new ProbeContext(options);
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        context.Rows.Add(new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "wrong-provider" });
        await context.SaveChangesAsync();
        observer.Clear();
        var query = context.Rows.Where(row => row.Id == 1).ForUpdate();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync());
        Assert.Contains(nameof(RowLockExtensions.ForUpdate), exception.Message, StringComparison.Ordinal);
        Assert.Contains("could not be translated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, observer.Count);
        QueryTestSupport.WriteOutcome(output, "Q17-wrong-provider", candidate, "explicit-reject-as-designed",
            $"reason={exception.Message};issuedCommands={observer.Count}");
    }

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Q17_replacing_generator_after_compiled_query_cache_population_fails_closed(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "cached" });

        Func<ProbeContext, int, ProbeRow> compiled = EF.CompileQuery(
            (ProbeContext context, int id) => context.Rows.Where(row => row.Id == id).ForUpdate().Single());

        var warmObserver = new SqlCommandObserver();
        IModel model;
        await using (var warm = QueryTestSupport.CreateContext(fixture, candidate, warmObserver))
        await using (var transaction = await warm.Database.BeginTransactionAsync())
        {
            model = warm.Model;
            var selected = compiled(warm, 1);
            Assert.Equal(1, selected.Id);
            Assert.Contains("FOR UPDATE OF", warmObserver.Commands.Single(), StringComparison.Ordinal);
            await transaction.CommitAsync();
        }

        var replacedObserver = new SqlCommandObserver();
        await using var replaced = new ProbeContext(BuildOptions(candidate, replacedObserver, replaceGenerator: true, model: model));
        await using var replacedTransaction = await replaced.Database.BeginTransactionAsync();
        var failure = Assert.Throws<InvalidOperationException>(() => compiled(replaced, 1));
        Assert.StartsWith("The PostgreSQL row-lock request cannot be fulfilled:", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, replacedObserver.Count);
        QueryTestSupport.WriteOutcome(output, "Q17-cache-then-overwrite", candidate, "explicit-reject-as-designed",
            $"warmSql={OneLine(warmObserver.Commands.Single())};reason={failure.Message};issuedCommands={replacedObserver.Count}");
        await replacedTransaction.RollbackAsync();
    }

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Q17_replacing_translator_before_compile_and_after_cache_fails_closed(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "translator-replacement" });

        var beforeObserver = new SqlCommandObserver();
        await using (var before = new ProbeContext(BuildOptions(
            candidate, beforeObserver, replaceGenerator: false, replaceTranslator: true)))
        await using (var transaction = await before.Database.BeginTransactionAsync())
        {
            var query = before.Rows.Where(row => row.Id == 1).ForUpdate();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync());
            Assert.StartsWith("The PostgreSQL row-lock request cannot be fulfilled:", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, beforeObserver.Count);
            QueryTestSupport.WriteOutcome(output, "Q17-translator-replaced-before-compile", candidate,
                "explicit-reject-as-designed", $"reason={failure.Message};issuedCommands={beforeObserver.Count}");
        }

        Func<ProbeContext, int, ProbeRow> compiled = EF.CompileQuery(
            (ProbeContext context, int id) => context.Rows.Where(row => row.Id == id).ForUpdate().Single());
        var warmObserver = new SqlCommandObserver();
        IModel model;
        await using (var warm = QueryTestSupport.CreateContext(fixture, candidate, warmObserver))
        await using (var transaction = await warm.Database.BeginTransactionAsync())
        {
            model = warm.Model;
            var selected = compiled(warm, 1);
            Assert.Equal(1, selected.Id);
            Assert.Contains("FOR UPDATE OF", warmObserver.Commands.Single(), StringComparison.Ordinal);
            await transaction.RollbackAsync();
        }

        var cachedObserver = new SqlCommandObserver();
        await using var cached = new ProbeContext(BuildOptions(
            candidate, cachedObserver, replaceGenerator: false, model: model, replaceTranslator: true));
        await using var cachedTransaction = await cached.Database.BeginTransactionAsync();
        var cachedFailure = Assert.Throws<InvalidOperationException>(() => compiled(cached, 1));
        Assert.StartsWith("The PostgreSQL row-lock request cannot be fulfilled:", cachedFailure.Message, StringComparison.Ordinal);
        Assert.Equal(0, cachedObserver.Count);
        QueryTestSupport.WriteOutcome(output, "Q17-translator-replaced-after-cache", candidate,
            "explicit-reject-as-designed", $"warmSql={OneLine(warmObserver.Commands.Single())};reason={cachedFailure.Message};issuedCommands={cachedObserver.Count}");
        await cachedTransaction.RollbackAsync();
    }

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Q18_unmarked_queries_match_native_sql_and_results(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "native" });

        var nativeObserver = new SqlCommandObserver();
        await using var native = QueryTestSupport.CreateContext(fixture, null, nativeObserver);
        var nativeQuery = native.Rows.Where(row => row.Id == 1);
        var nativeSql = nativeQuery.ToQueryString();
        var nativeResult = await nativeQuery.SingleAsync();

        var enabledObserver = new SqlCommandObserver();
        await using var enabled = QueryTestSupport.CreateContext(fixture, candidate, enabledObserver);
        var enabledQuery = enabled.Rows.Where(row => row.Id == 1);
        var enabledSql = enabledQuery.ToQueryString();
        var enabledResult = await enabledQuery.SingleAsync();

        Assert.Equal(nativeSql, enabledSql);
        Assert.Equal(nativeResult.Id, enabledResult.Id);
        Assert.Equal(nativeResult.Payload, enabledResult.Payload);
        Assert.DoesNotContain("FOR UPDATE", enabledObserver.Commands.Single(), StringComparison.Ordinal);
        QueryTestSupport.WriteOutcome(output, "Q18", candidate, "supported-pass",
            $"nativeSql={OneLine(nativeObserver.Commands.Single())};enabledSql={OneLine(enabledObserver.Commands.Single())};ids={nativeResult.Id},{enabledResult.Id};payloads={nativeResult.Payload},{enabledResult.Payload}");
    }

    private DbContextOptions<ProbeContext> BuildOptions(
        PocCarrier candidate,
        SqlCommandObserver observer,
        bool replaceGenerator,
        IModel? model = null,
        bool replaceTranslator = false)
    {
        var builder = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions(candidate))
            .AddInterceptors(observer);

        if (replaceGenerator)
        {
            builder.ReplaceService<IQuerySqlGeneratorFactory, PlainQuerySqlGeneratorFactory>();
        }

        if (replaceTranslator)
        {
            builder.ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory,
                ReplacementNpgsqlQueryMethodTranslatingExpressionVisitorFactory>();
        }

        if (model is not null)
        {
            builder.UseModel(model);
        }

        return builder.Options;
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

internal sealed class PlainQuerySqlGeneratorFactory(QuerySqlGeneratorDependencies dependencies)
    : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create() => new(dependencies);
}

#pragma warning disable EF1001
internal sealed class ReplacementNpgsqlQueryMethodTranslatingExpressionVisitorFactory(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
    INpgsqlSingletonOptions npgsqlSingletonOptions)
    : NpgsqlQueryableMethodTranslatingExpressionVisitorFactory(
        dependencies,
        relationalDependencies,
        npgsqlSingletonOptions);
#pragma warning restore EF1001
