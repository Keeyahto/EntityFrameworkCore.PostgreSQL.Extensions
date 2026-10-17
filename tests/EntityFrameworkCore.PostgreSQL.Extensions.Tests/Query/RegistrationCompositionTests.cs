using EntityFrameworkCore.PostgreSQL.Extensions;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;
using Xunit.Abstractions;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class RegistrationCompositionTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Repeated_registration_is_idempotent_and_conflicting_carriers_fail_loudly(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "repeat" });

        var observer = new SqlCommandObserver();
        var options = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql =>
            {
                npgsql.UsePostgreSqlExtensions(candidate);
                npgsql.UsePostgreSqlExtensions(candidate);
            })
            .AddInterceptors(observer)
            .Options;
        await using var context = new ProbeContext(options);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var result = await context.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync();
        Assert.Equal(1, result.Id);
        Assert.Single(observer.Commands);
        Assert.Contains("FOR UPDATE OF", observer.Commands.Single(), StringComparison.Ordinal);
        await transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "Q17-repeated-registration", candidate, "supported-pass",
            $"executed={OneLine(observer.Commands.Single())};ids={result.Id};issuedCommands={observer.Count}");

        var conflict = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings()
                .UseNpgsql(fixture.ConnectionString, npgsql =>
                {
                    npgsql.UsePostgreSqlExtensions(candidate);
                    npgsql.UsePostgreSqlExtensions(Next(candidate));
                });
        });
        Assert.Contains("Conflicting PostgreSQL extension query carriers", conflict.Message, StringComparison.Ordinal);
        output.WriteLine($"Q17-conflicting-registration|{candidate}|expected=explicit-reject-as-designed|observed=explicit-reject-as-designed|reason={conflict.Message};issuedCommands=0");
    }

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Context_factory_and_pool_reuse_do_not_leak_lock_intent(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "locked" },
            new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "ordinary" });

        var observer = new SqlCommandObserver();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ProbeContext>(options =>
        {
            options.UsePocMatrixWarnings().UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions(candidate));
            options.AddInterceptors(observer);
        });
        await using var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IDbContextFactory<ProbeContext>>();
        await using (var context = await factory.CreateDbContextAsync())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var locked = await context.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync();
            Assert.Equal(1, locked.Id);
            Assert.Contains("FOR UPDATE OF", observer.Commands.Single(), StringComparison.Ordinal);
            await transaction.CommitAsync();
        }

        await using (var pooledContext = await factory.CreateDbContextAsync())
        {
            var ordinary = await pooledContext.Rows.Where(row => row.Id == 2).SingleAsync();
            Assert.Equal(2, ordinary.Id);
            Assert.DoesNotContain("FOR UPDATE", observer.Commands.Last(), StringComparison.Ordinal);
        }

        var pooledOptions = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions(candidate))
            .AddInterceptors(observer)
            .Options;
        var pool = new PooledDbContextFactory<ProbeContext>(pooledOptions, poolSize: 1);
        await using (var context = await pool.CreateDbContextAsync())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var locked = await context.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync();
            Assert.Equal(1, locked.Id);
            await transaction.CommitAsync();
        }

        await using (var context = await pool.CreateDbContextAsync())
        {
            var ordinary = await context.Rows.Where(row => row.Id == 2).SingleAsync();
            Assert.Equal(2, ordinary.Id);
            Assert.DoesNotContain("FOR UPDATE", observer.Commands.Last(), StringComparison.Ordinal);
        }

        QueryTestSupport.WriteOutcome(output, "Q15/Q17-factory-pool", candidate, "supported-pass",
            $"factoryLocked={OneLine(observer.Commands[0])};factoryUnmarked={OneLine(observer.Commands[1])};poolLocked={OneLine(observer.Commands[2])};poolUnmarked={OneLine(observer.Commands[3])};commandCount={observer.Count}");
    }

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Noda_time_additive_translator_coexists_with_lock_registration(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "nodatime" });

        var observer = new SqlCommandObserver();
        var options = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql =>
            {
                npgsql.UseNodaTime();
                npgsql.UsePostgreSqlExtensions(candidate);
            })
            .AddInterceptors(observer)
            .Options;
        await using var context = new ProbeContext(options);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var selected = await context.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync();
        Assert.Equal(1, selected.Id);
        Assert.Contains("FOR UPDATE OF", observer.Commands.Single(), StringComparison.Ordinal);
        var serverInstant = await context.Rows.Where(row => row.Id == 1)
            .Select(_ => SystemClock.Instance.GetCurrentInstant())
            .SingleAsync();
        Assert.NotEqual(Instant.MinValue, serverInstant);
        Assert.Contains("now()", observer.Commands.Last(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOR UPDATE", observer.Commands.Last(), StringComparison.Ordinal);
        await transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "Q17-NodaTime-coexistence", candidate, "supported-pass",
            $"lockSql={OneLine(observer.Commands.First())};nodaTimeSql={OneLine(observer.Commands.Last())};ids={selected.Id};serverInstant={serverInstant};nodaTime=registered-and-translated;release=rollback");
    }

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task Separate_context_types_keep_configured_postgresql_targets_and_query_services_isolated(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "target-14" },
            new ProbeRow { Id = 2, PeerId = 8, Rank = 20, Payload = "target-15" });

        var target14Observer = new SqlCommandObserver();
        var target14Options = new DbContextOptionsBuilder<ProbeContextV14>().UsePocMatrixWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql =>
            {
                npgsql.SetPostgresVersion(14, 0);
                npgsql.UsePostgreSqlExtensions(candidate);
            })
            .AddInterceptors(target14Observer)
            .Options;
        await using var target14 = new ProbeContextV14(target14Options);
        await using var target14Transaction = await target14.Database.BeginTransactionAsync();
        var locked = await target14.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync();
        Assert.Equal(1, locked.Id);
        Assert.Contains("FOR UPDATE OF", target14Observer.Commands.Single(), StringComparison.Ordinal);

        var target15Observer = new SqlCommandObserver();
        var target15Options = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql =>
            {
                npgsql.SetPostgresVersion(15, 0);
                npgsql.UsePostgreSqlExtensions(candidate);
            })
            .AddInterceptors(target15Observer)
            .Options;
        await using var target15 = new ProbeContext(target15Options);
        var ordinary = await target15.Rows.Where(row => row.Id == 2).SingleAsync();
        Assert.Equal(2, ordinary.Id);
        Assert.DoesNotContain("FOR UPDATE", target15Observer.Commands.Single(), StringComparison.Ordinal);

        await target14Transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "Q17-context-target-isolation", candidate, "supported-pass",
            $"contextTypes={nameof(ProbeContextV14)},{nameof(ProbeContext)};configuredTargets=14.0,15.0;target14LockSql={OneLine(target14Observer.Commands.Single())};target15NativeSql={OneLine(target15Observer.Commands.Single())};ids={locked.Id},{ordinary.Id};commandCounts={target14Observer.Count},{target15Observer.Count}");
    }

    [Theory]
    [MemberData(nameof(AdmittedRootShapeTests.Candidates), MemberType = typeof(AdmittedRootShapeTests))]
    public async Task External_internal_service_provider_rejects_incompatible_replacements_without_select(PocCarrier candidate)
    {
        await QueryTestSupport.SeedAsync(fixture,
            new ProbeRow { Id = 1, PeerId = 7, Rank = 10, Payload = "external-provider" });

        var services = new ServiceCollection();
        services.AddEntityFrameworkNpgsql();
        using var externalProvider = services.BuildServiceProvider();
        var observer = new SqlCommandObserver();
        var options = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions(candidate))
            .UseInternalServiceProvider(externalProvider)
            .AddInterceptors(observer)
            .Options;

        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var context = new ProbeContext(options);
            await using var transaction = await context.Database.BeginTransactionAsync();
            _ = await context.Rows.Where(row => row.Id == 1).ForUpdate().SingleAsync();
        });
        var providerFailure = Assert.IsType<InvalidOperationException>(failure);
        Assert.True(
            providerFailure.Message.Contains("internal service provider", StringComparison.OrdinalIgnoreCase)
            || providerFailure.Message.Contains("service replacement", StringComparison.OrdinalIgnoreCase)
            || providerFailure.Message.Contains("replaced service", StringComparison.OrdinalIgnoreCase),
            $"Expected a specific incompatibility reason, got: {providerFailure.Message}");
        Assert.Equal(0, observer.Count);
        QueryTestSupport.WriteOutcome(output, "Q17-external-internal-provider", candidate, "explicit-reject-as-designed",
            $"reason={providerFailure.Message};issuedCommands={observer.Count}");
    }

    private static PocCarrier Next(PocCarrier candidate) => candidate switch
    {
        PocCarrier.TableAnnotations => PocCarrier.TranslatorTag,
        PocCarrier.TranslatorTag => PocCarrier.ExpressionTag,
        PocCarrier.ExpressionTag => PocCarrier.TableAnnotations,
        _ => throw new ArgumentOutOfRangeException(nameof(candidate))
    };

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
