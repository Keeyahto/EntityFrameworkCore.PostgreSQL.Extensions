using EntityFrameworkCore.PostgreSQL.Extensions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;

[CollectionDefinition("PostgreSQL integration", DisableParallelization = true)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL integration";
}

/// <summary>
/// Owns one isolated PostgreSQL 16 instance for real-provider tests.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string ImageReference = "postgres:16-alpine@sha256:33f923b05f64ca54ac4401c01126a6b92afe839a0aa0a52bc5aeb5cc958e5f20";
    private static string? _observedServerVersion;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(ImageReference)
        .WithDatabase("pgext_tests")
        .WithUsername("pgext_tests")
        .WithPassword($"poc-{Guid.NewGuid():N}-{Guid.NewGuid():N}")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public static string ObservedServerVersion => _observedServerVersion ?? "not-observed";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        var serverVersion = await context.Database
            .SqlQueryRaw<int>("SELECT current_setting('server_version_num')::int AS \"Value\"")
            .SingleAsync();
        _observedServerVersion = await context.Database
            .SqlQueryRaw<string>("SELECT current_setting('server_version') AS \"Value\"")
            .SingleAsync();
        if (serverVersion / 10000 != 16)
        {
            throw new InvalidOperationException($"The PostgreSQL fixture must run major version 16, actual server_version_num={serverVersion}.");
        }
    }

    public ProbeContext CreateContext(PocCarrier? candidate = null)
    {
        var options = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings()
            .UseNpgsql(ConnectionString, npgsql =>
            {
                if (candidate is { } selectedCarrier)
                {
                    npgsql.UsePostgreSqlExtensions(selectedCarrier);
                }
            })
            .Options;

        return new ProbeContext(options);
    }

    public async Task ResetAsync()
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Children\", \"Rows\", \"Peers\"");
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
