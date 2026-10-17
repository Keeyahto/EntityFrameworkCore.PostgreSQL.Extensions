using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using EntityFrameworkCore.PostgreSQL.Extensions;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Xunit;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

[Trait("Category", "Unit")]
public sealed class CarrierContractUnitTests
{
    public static TheoryData<PocCarrier> Candidates => new()
    {
        PocCarrier.TableAnnotations,
        PocCarrier.TranslatorTag,
        PocCarrier.ExpressionTag
    };

    [Fact]
    public void Public_marker_remains_a_method_call_without_provider_registration()
    {
        var source = new[] { new object() }.AsQueryable();

        var marked = source.ForUpdate();

        var methodCall = Assert.IsAssignableFrom<MethodCallExpression>(marked.Expression);
        Assert.Equal(nameof(RowLockExtensions.ForUpdate), methodCall.Method.Name);
        Assert.Equal(typeof(RowLockExtensions), methodCall.Method.DeclaringType);
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public void Existing_generator_replacement_is_rejected_during_extension_registration(PocCarrier candidate)
    {
        const string connectionString = "Host=127.0.0.1;Database=unit;Username=unit;Password=unit";
        var optionsBuilder = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings().UseNpgsql(connectionString);
        optionsBuilder.ReplaceService<IQuerySqlGeneratorFactory, PlainQuerySqlGeneratorFactory>();

        var exception = Assert.Throws<InvalidOperationException>(() => optionsBuilder.UseNpgsql(
            connectionString, npgsql => npgsql.UsePostgreSqlExtensions(candidate)));

        Assert.Contains(nameof(IQuerySqlGeneratorFactory), exception.Message, StringComparison.Ordinal);
        Assert.Contains("already has a service replacement", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public void Existing_translator_replacement_is_rejected_during_extension_registration(PocCarrier candidate)
    {
        const string connectionString = "Host=127.0.0.1;Database=unit;Username=unit;Password=unit";
        var optionsBuilder = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings().UseNpgsql(connectionString);
        optionsBuilder.ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory,
            ReplacementNpgsqlQueryMethodTranslatingExpressionVisitorFactory>();

        var exception = Assert.Throws<InvalidOperationException>(() => optionsBuilder.UseNpgsql(
            connectionString, npgsql => npgsql.UsePostgreSqlExtensions(candidate)));

        Assert.Contains(nameof(IQueryableMethodTranslatingExpressionVisitorFactory), exception.Message, StringComparison.Ordinal);
        Assert.Contains("already has a service replacement", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_specific_generator_replacement_is_rejected_during_extension_registration()
    {
        const string connectionString = "Host=127.0.0.1;Database=unit;Username=unit;Password=unit";
        var optionsBuilder = new DbContextOptionsBuilder<ProbeContext>().UsePocMatrixWarnings().UseNpgsql(connectionString);
        optionsBuilder.ReplaceService<IQuerySqlGeneratorFactory,
            PlainQuerySqlGeneratorFactory, PlainQuerySqlGeneratorFactory>();

        var exception = Assert.Throws<InvalidOperationException>(() => optionsBuilder.UseNpgsql(
            connectionString, npgsql => npgsql.UsePostgreSqlExtensions(PocCarrier.TableAnnotations)));

        Assert.Contains(nameof(IQuerySqlGeneratorFactory), exception.Message, StringComparison.Ordinal);
        Assert.Contains("already has a service replacement", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_expression_is_sealed_and_its_annotation_copy_path_throws_at_runtime()
    {
        Assert.True(typeof(SelectExpression).IsSealed);

        var method = typeof(SelectExpression).GetMethod(
            "WithAnnotations",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var selectExpression = RuntimeHelpers.GetUninitializedObject(typeof(SelectExpression));
        var annotationType = method!.GetParameters().Single().ParameterType;
        var emptyAnnotations = Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(annotationType.GetGenericArguments()));
        var exception = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(selectExpression, new[] { emptyAnnotations }));

        Assert.IsType<UnreachableException>(exception.InnerException);
    }
}
