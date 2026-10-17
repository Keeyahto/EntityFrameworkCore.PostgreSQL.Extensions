using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Mechanisms;

[Collection(PostgreSqlCollection.Name)]
public sealed class CapabilityProbeTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task ReportsConfiguredSqlTargetSeparatelyFromActualServerAndOnlyChecksRequiredExtensions()
    {
        await using var context = CreateNpgsqlContext();

        var report = await PostgreSqlCapabilityProbe.CheckAsync(
            context,
            new[] { new PostgreSqlExtensionRequirement("plpgsql", "1.0") });

        Assert.Equal(PostgreSqlCapabilityOutcome.Compatible, report.Outcome);
        Assert.Equal(new Version(14, 0), report.SqlTargetVersion);
        Assert.Equal(16, report.ActualServerVersion?.Major);
        Assert.Contains(report.InstalledExtensions, extension => extension.Name == "plpgsql");
        Assert.Empty(report.UnmetRequirements);
    }

    [Fact]
    public async Task MissingDeclaredRequirementIsIncompatibleButUnusedMissingExtensionIsIrrelevant()
    {
        await using var context = CreateNpgsqlContext();

        var noRequirements = await PostgreSqlCapabilityProbe.CheckAsync(context, Array.Empty<PostgreSqlExtensionRequirement>());
        var requiredMissing = await PostgreSqlCapabilityProbe.CheckAsync(
            context,
            new[] { new PostgreSqlExtensionRequirement("pgext_test_extension_that_is_not_installed") });

        Assert.Equal(PostgreSqlCapabilityOutcome.Compatible, noRequirements.Outcome);
        Assert.Equal(PostgreSqlCapabilityOutcome.Incompatible, requiredMissing.Outcome);
        Assert.Single(requiredMissing.UnmetRequirements);
    }

    [Fact]
    public async Task ReadsAnExplicitNpgsqlSqlTargetWithoutChangingIt()
    {
        var options = new DbContextOptionsBuilder<DbContext>().UsePocMatrixWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.SetPostgresVersion(15, 0))
            .Options;
        await using var context = new DbContext(options);

        var report = await PostgreSqlCapabilityProbe.CheckAsync(context, Array.Empty<PostgreSqlExtensionRequirement>());

        Assert.Equal(new Version(15, 0), report.SqlTargetVersion);
        Assert.Equal(16, report.ActualServerVersion?.Major);
    }

    [Fact]
    public async Task TargetNewerThanActualServerIsIncompatible()
    {
        var options = new DbContextOptionsBuilder<DbContext>().UsePocMatrixWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.SetPostgresVersion(17, 0))
            .Options;
        await using var context = new DbContext(options);

        var report = await PostgreSqlCapabilityProbe.CheckAsync(context, Array.Empty<PostgreSqlExtensionRequirement>());

        Assert.Equal(PostgreSqlCapabilityOutcome.Incompatible, report.Outcome);
        Assert.Equal(new Version(17, 0), report.SqlTargetVersion);
        Assert.Equal(16, report.ActualServerVersion?.Major);
    }

    [Fact]
    public async Task ConnectionFailureIsIndeterminateAndCallerCancellationPropagates()
    {
        var unreachable = new DbContextOptionsBuilder<DbContext>().UsePocMatrixWarnings()
            .UseNpgsql("Host=127.0.0.1;Port=1;Username=none;Password=none;Database=none;Timeout=1")
            .Options;
        await using var failedContext = new DbContext(unreachable);
        var report = await PostgreSqlCapabilityProbe.CheckAsync(failedContext, Array.Empty<PostgreSqlExtensionRequirement>());

        Assert.Equal(PostgreSqlCapabilityOutcome.Indeterminate, report.Outcome);
        Assert.NotNull(report.FailureType);

        await using var context = CreateNpgsqlContext();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PostgreSqlCapabilityProbe.CheckAsync(context, Array.Empty<PostgreSqlExtensionRequirement>(), cancelled.Token));
    }

    [Fact]
    public async Task WrongProviderReturnsIncompatibleWithoutOpeningItsConnection()
    {
        var options = new DbContextOptionsBuilder<DbContext>().UsePocMatrixWarnings().UseSqlite("Data Source=:memory:").Options;
        await using var context = new DbContext(options);

        var report = await PostgreSqlCapabilityProbe.CheckAsync(context, Array.Empty<PostgreSqlExtensionRequirement>());

        Assert.Equal(PostgreSqlCapabilityOutcome.Incompatible, report.Outcome);
        Assert.Null(report.SqlTargetVersion);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    private DbContext CreateNpgsqlContext()
        => new(new DbContextOptionsBuilder<DbContext>().UsePocMatrixWarnings().UseNpgsql(fixture.ConnectionString).Options);
}
