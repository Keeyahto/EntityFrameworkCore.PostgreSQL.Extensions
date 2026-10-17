using System.Linq.Expressions;
using EntityFrameworkCore.PostgreSQL.Extensions;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Xunit;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

[Trait("Category", "Unit")]
public sealed class ProductionQueryRegistrationTests
{

[Fact]
    public void Public_marker_remains_a_method_call_without_provider_registration()
    {
        var source = new[] { new object() }.AsQueryable();

        var marked = source.ForUpdate();

        var methodCall = Assert.IsAssignableFrom<MethodCallExpression>(marked.Expression);
        Assert.Equal(nameof(RowLockExtensions.ForUpdate), methodCall.Method.Name);
        Assert.Equal(typeof(RowLockExtensions), methodCall.Method.DeclaringType);
    }

    [Fact]
    public void Existing_generator_replacement_is_rejected_during_extension_registration()
    {
        const string connectionString = "Host=127.0.0.1;Database=unit;Username=unit;Password=unit";
        var optionsBuilder = new DbContextOptionsBuilder<ProbeContext>().UseProviderTestWarnings().UseNpgsql(connectionString);
        optionsBuilder.ReplaceService<IQuerySqlGeneratorFactory, PlainQuerySqlGeneratorFactory>();

        var exception = Assert.Throws<InvalidOperationException>(() => optionsBuilder.UseNpgsql(
            connectionString, npgsql => npgsql.UsePostgreSqlExtensions()));

        Assert.Contains(nameof(IQuerySqlGeneratorFactory), exception.Message, StringComparison.Ordinal);
        Assert.Contains("competing service replacement", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_translator_replacement_is_rejected_during_extension_registration()
    {
        const string connectionString = "Host=127.0.0.1;Database=unit;Username=unit;Password=unit";
        var optionsBuilder = new DbContextOptionsBuilder<ProbeContext>().UseProviderTestWarnings().UseNpgsql(connectionString);
        optionsBuilder.ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory,
            ReplacementNpgsqlQueryMethodTranslatingExpressionVisitorFactory>();

        var exception = Assert.Throws<InvalidOperationException>(() => optionsBuilder.UseNpgsql(
            connectionString, npgsql => npgsql.UsePostgreSqlExtensions()));

        Assert.Contains(nameof(IQueryableMethodTranslatingExpressionVisitorFactory), exception.Message, StringComparison.Ordinal);
        Assert.Contains("competing service replacement", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_specific_generator_replacement_is_rejected_during_extension_registration()
    {
        const string connectionString = "Host=127.0.0.1;Database=unit;Username=unit;Password=unit";
        var optionsBuilder = new DbContextOptionsBuilder<ProbeContext>().UseProviderTestWarnings().UseNpgsql(connectionString);
        optionsBuilder.ReplaceService<IQuerySqlGeneratorFactory,
            PlainQuerySqlGeneratorFactory, PlainQuerySqlGeneratorFactory>();

        var exception = Assert.Throws<InvalidOperationException>(() => optionsBuilder.UseNpgsql(
            connectionString, npgsql => npgsql.UsePostgreSqlExtensions()));

        Assert.Contains(nameof(IQuerySqlGeneratorFactory), exception.Message, StringComparison.Ordinal);
        Assert.Contains("competing service replacement", exception.Message, StringComparison.Ordinal);
    }

}

