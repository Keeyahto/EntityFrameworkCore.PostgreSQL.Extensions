using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

// EF1001 is scoped to this file because A/B must preserve the native Npgsql translator and its nested-query behavior.
#pragma warning disable EF1001
internal abstract class RowLockQueryVisitorFactory : NpgsqlQueryableMethodTranslatingExpressionVisitorFactory
{
    private readonly INpgsqlSingletonOptions _npgsqlSingletonOptions;
    private readonly PocCarrier _carrier;

    protected RowLockQueryVisitorFactory(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        INpgsqlSingletonOptions npgsqlSingletonOptions,
        PocCarrier carrier)
        : base(dependencies, relationalDependencies, npgsqlSingletonOptions)
    {
        _npgsqlSingletonOptions = npgsqlSingletonOptions;
        _carrier = carrier;
    }

    public override QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => new RowLockQueryableMethodTranslatingExpressionVisitor(
            Dependencies,
            RelationalDependencies,
            queryCompilationContext as RelationalQueryCompilationContext
                ?? throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected("the active query compilation context is not relational"),
            _npgsqlSingletonOptions,
            _carrier);
}

internal sealed class AnnotationLockQueryVisitorFactory : RowLockQueryVisitorFactory
{
    public AnnotationLockQueryVisitorFactory(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies, relationalDependencies, npgsqlSingletonOptions, PocCarrier.TableAnnotations)
    {
    }
}

internal sealed class TagLockQueryVisitorFactory : RowLockQueryVisitorFactory
{
    public TagLockQueryVisitorFactory(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies, relationalDependencies, npgsqlSingletonOptions, PocCarrier.TranslatorTag)
    {
    }
}

internal sealed class RowLockQueryableMethodTranslatingExpressionVisitor : NpgsqlQueryableMethodTranslatingExpressionVisitor
{
    private readonly PocCarrier _carrier;
    private readonly RelationalQueryCompilationContext _queryCompilationContext;

    public RowLockQueryableMethodTranslatingExpressionVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        RelationalQueryCompilationContext queryCompilationContext,
        INpgsqlSingletonOptions npgsqlSingletonOptions,
        PocCarrier carrier)
        : base(dependencies, relationalDependencies, queryCompilationContext, npgsqlSingletonOptions)
    {
        _carrier = carrier;
        _queryCompilationContext = queryCompilationContext;
    }

    private RowLockQueryableMethodTranslatingExpressionVisitor(RowLockQueryableMethodTranslatingExpressionVisitor parent)
        : base(parent)
    {
        _carrier = parent._carrier;
        _queryCompilationContext = parent._queryCompilationContext;
    }

    protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor()
        => new RowLockQueryableMethodTranslatingExpressionVisitor(this);

    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        if (methodCallExpression.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
            && methodCallExpression.Method.Name == nameof(EntityFrameworkQueryableExtensions.TagWith)
            && methodCallExpression.Arguments.Count == 2
            && methodCallExpression.Arguments[1] is ConstantExpression { Value: string suppliedTag }
            && LockProtocol.ContainsReservedProtocolText(suppliedTag))
        {
            throw Rejected("a user query tag spoofs the reserved lock protocol");
        }

        if (!RowLockExtensions.TryRead(methodCallExpression, out var request))
        {
            return base.VisitMethodCall(methodCallExpression);
        }

        if (methodCallExpression.Arguments.Count == 0
            || Visit(methodCallExpression.Arguments[0]) is not ShapedQueryExpression shapedQuery
            || shapedQuery.QueryExpression is not SelectExpression select)
        {
            throw Rejected("the lock marker must wrap a relational entity query");
        }

        var table = ValidateRootEntity(shapedQuery, select);
        var tag = request.Tag(_carrier);
        _queryCompilationContext.AddTag(tag);
        if (_carrier == PocCarrier.TableAnnotations)
        {
            // EF1001: this exact EF Core 10 internal hook is the experimental table-occurrence carrier under comparison.
#pragma warning disable EF1001
            var annotatedTable = table.AddAnnotation(LockProtocol.AnnotationName, request);
            select.SetTables(new TableExpressionBase[] { annotatedTable });
#pragma warning restore EF1001
        }

        return shapedQuery;
    }

    private static TableExpression ValidateRootEntity(ShapedQueryExpression shapedQuery, SelectExpression select)
    {
        if (shapedQuery.ShaperExpression is not StructuralTypeShaperExpression shaper
            || shaper.StructuralType is not IEntityType entityType
            || select.Tables.Count != 1
            || select.Tables[0] is not TableExpression table
            || table.Table.EntityTypeMappings.All(mapping => !ReferenceEquals(mapping.TypeBase, entityType)))
        {
            throw Rejected("only one unambiguous mapped root entity table is supported");
        }

        return table;
    }

    internal static InvalidOperationException Rejected(string reason)
        => new($"The PostgreSQL row-lock request cannot be fulfilled: {reason}.");
}
#pragma warning restore EF1001
