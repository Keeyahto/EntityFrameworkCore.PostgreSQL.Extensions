using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;

internal static class ProviderTestOptions
{
    public static DbContextOptionsBuilder<TContext> UseProviderTestWarnings<TContext>(
        this DbContextOptionsBuilder<TContext> builder) where TContext : DbContext
    {
        Configure(builder);
        return builder;
    }

    public static DbContextOptionsBuilder UseProviderTestWarnings(this DbContextOptionsBuilder builder)
    {
        Configure(builder);
        return builder;
    }

    private static void Configure(DbContextOptionsBuilder builder)
    {
        // Keep native provider/query caching and all other warnings; this suite intentionally exercises
        // several independent provider compositions and a size-one pool.
        builder.ConfigureWarnings(warnings => warnings.Log(CoreEventId.ManyServiceProvidersCreatedWarning));
    }
}
