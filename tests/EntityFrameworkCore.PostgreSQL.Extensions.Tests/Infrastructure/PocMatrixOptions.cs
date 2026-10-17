using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;

internal static class PocMatrixOptions
{
    public static DbContextOptionsBuilder<TContext> UsePocMatrixWarnings<TContext>(
        this DbContextOptionsBuilder<TContext> builder) where TContext : DbContext
    {
        Configure(builder);
        return builder;
    }

    public static DbContextOptionsBuilder UsePocMatrixWarnings(this DbContextOptionsBuilder builder)
    {
        Configure(builder);
        return builder;
    }

    private static void Configure(DbContextOptionsBuilder builder)
    {
        // The comparison deliberately has more than twenty distinct provider configurations.
        // Keep native provider/query caching and all other warnings; log this matrix-size warning.
        // Configure before context construction and pooling freeze, including factory callbacks.
        builder.ConfigureWarnings(warnings => warnings.Log(CoreEventId.ManyServiceProvidersCreatedWarning));
    }
}
