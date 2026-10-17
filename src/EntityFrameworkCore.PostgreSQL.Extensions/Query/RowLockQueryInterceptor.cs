using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

// EF1001 is scoped to this file for exact pinned Npgsql service-identity validation at command execution.
#pragma warning disable EF1001
internal sealed class RowLockQueryInterceptor : DbCommandInterceptor, IQueryExpressionInterceptor
{
    private static readonly RowLockQueryInterceptor[] Instances =
        Enum.GetValues<PocCarrier>().Select(carrier => new RowLockQueryInterceptor(carrier)).ToArray();
    private static readonly MethodInfo TagWithMethod = typeof(EntityFrameworkQueryableExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(EntityFrameworkQueryableExtensions.TagWith)
            && method.IsGenericMethodDefinition
            && method.GetParameters().Length == 2);

    private readonly PocCarrier _carrier;

    private RowLockQueryInterceptor(PocCarrier carrier) => _carrier = carrier;

    public static RowLockQueryInterceptor For(PocCarrier carrier) => Instances[(int)carrier];

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        var markers = MarkerCollector.Collect(queryExpression);
        if (markers.Count == 0)
        {
            ValidateReservedTags(queryExpression, rejectDynamicTags: false);
            return queryExpression;
        }

        if (markers.Count != 1)
        {
            throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                "a query may contain only one row-lock request");
        }

        var context = eventData.Context
            ?? throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected("the query compilation context is unavailable");
        ValidateServices(context);
        ValidateSourceChain(markers[0].Call.Arguments[0]);
        ValidateOperators(queryExpression);
        ValidateReservedTags(queryExpression, rejectDynamicTags: true);
        var request = markers[0].Request;
        return _carrier == PocCarrier.ExpressionTag
            ? new ExpressionTagRewriter(request).Visit(queryExpression)!
            : queryExpression;
    }

    private static void ValidateOperators(Expression expression)
    {
        new OperatorValidator().Visit(expression);
    }

    private static void ValidateReservedTags(Expression expression, bool rejectDynamicTags)
    {
        new ReservedTagValidator(rejectDynamicTags).Visit(expression);
    }

    private static void ValidateSourceChain(Expression expression)
    {
        while (expression is MethodCallExpression call)
        {
            var allowed = call.Method.DeclaringType == typeof(Queryable)
                    && call.Method.Name is nameof(Queryable.Where)
                        or nameof(Queryable.OrderBy)
                        or nameof(Queryable.OrderByDescending)
                        or nameof(Queryable.ThenBy)
                        or nameof(Queryable.ThenByDescending)
                        or nameof(Queryable.Take)
                        or nameof(Queryable.First)
                        or nameof(Queryable.FirstOrDefault)
                        or nameof(Queryable.Single)
                        or nameof(Queryable.SingleOrDefault)
                || call.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                    && call.Method.Name is nameof(EntityFrameworkQueryableExtensions.AsNoTracking)
                        or nameof(EntityFrameworkQueryableExtensions.AsTracking)
                        or nameof(EntityFrameworkQueryableExtensions.TagWith)
                        or nameof(EntityFrameworkQueryableExtensions.FirstAsync)
                        or nameof(EntityFrameworkQueryableExtensions.FirstOrDefaultAsync)
                        or nameof(EntityFrameworkQueryableExtensions.SingleAsync)
                        or nameof(EntityFrameworkQueryableExtensions.SingleOrDefaultAsync);
            if (!allowed || call.Arguments.Count == 0)
            {
                throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                    $"the source query operator '{call.Method.Name}' is outside the admitted root lock scope");
            }

            expression = call.Arguments[0];
        }
    }

    private void BeforeCommand(DbCommand command, CommandEventData eventData)
    {
        var requestTags = FindProtocolValues(command.CommandText, LockProtocol.TagPrefix);
        var receiptTags = FindProtocolValues(command.CommandText, LockProtocol.ReceiptPrefix);
        if (requestTags.Count == 0 && receiptTags.Count == 0)
        {
            return;
        }

        if (requestTags.Count != 1 || receiptTags.Count != 1 || requestTags[0] != receiptTags[0])
        {
            throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                "the generated command does not contain one matching lock request and fulfillment receipt");
        }

        var expected = $"{(int)_carrier}:";
        if (!requestTags[0].StartsWith(expected, StringComparison.Ordinal))
        {
            throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                "the command lock carrier does not match the configured context");
        }

        var context = eventData.Context
            ?? throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected("the execution context is unavailable");
        ValidateServices(context);
        var current = context.Database.CurrentTransaction?.GetDbTransaction();
        if (current is null
            || !ReferenceEquals(current, command.Transaction)
            || !ReferenceEquals(current.Connection, command.Connection))
        {
            throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                "execution requires the current borrowed EF transaction on the command connection");
        }
    }

    private void ValidateServices(DbContext context)
    {
        var generatorFactory = context.GetService<IQuerySqlGeneratorFactory>();
        var translatorFactory = context.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>();
        var expectedTranslator = _carrier switch
        {
            PocCarrier.TableAnnotations => translatorFactory.GetType() == typeof(AnnotationLockQueryVisitorFactory),
            PocCarrier.TranslatorTag => translatorFactory.GetType() == typeof(TagLockQueryVisitorFactory),
            PocCarrier.ExpressionTag => translatorFactory.GetType() == typeof(NpgsqlQueryableMethodTranslatingExpressionVisitorFactory),
            _ => false
        };
        if (generatorFactory.GetType() != typeof(RowLockQuerySqlGeneratorFactory) || !expectedTranslator)
        {
            throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                "the configured query generator or translator service was replaced after registration");
        }
    }

    private static List<string> FindProtocolValues(string commandText, string prefix)
    {
        var values = new List<string>();
        var commentPrefix = prefix == LockProtocol.TagPrefix ? "-- " : "/*";
        var marker = commentPrefix + prefix;
        var index = 0;
        while ((index = commandText.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            var start = index + marker.Length;
            var end = start;
            while (end < commandText.Length && commandText[end] is >= '0' and <= '9' or ':')
            {
                end++;
            }

            values.Add(commandText[start..end]);
            index = end;
        }

        return values;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        BeforeCommand(command, eventData);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        BeforeCommand(command, eventData);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        BeforeCommand(command, eventData);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        BeforeCommand(command, eventData);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        BeforeCommand(command, eventData);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        BeforeCommand(command, eventData);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private sealed class MarkerCollector : ExpressionVisitor
    {
        private readonly List<FoundMarker> _markers = new();
        public static List<FoundMarker> Collect(Expression expression)
        {
            var collector = new MarkerCollector();
            collector.Visit(expression);
            return collector._markers;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (RowLockExtensions.IsMarker(node.Method))
            {
                if (!RowLockExtensions.TryRead(node, out var request))
                {
                    throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected("the lock marker arguments are invalid");
                }

                _markers.Add(new FoundMarker(node, request));
            }

            return base.VisitMethodCall(node);
        }
    }

    private readonly record struct FoundMarker(MethodCallExpression Call, LockRequest Request);

    private sealed class OperatorValidator : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var containsSourceMarker = node.Arguments.Count > 0 && MarkerCollector.Collect(node.Arguments[0]).Count > 0;
            if (containsSourceMarker)
            {
                var isQueryable = node.Method.DeclaringType == typeof(Queryable)
                    || node.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions);
                var allowed = node.Method.Name is nameof(Queryable.Where)
                    or nameof(Queryable.OrderBy)
                    or nameof(Queryable.OrderByDescending)
                    or nameof(Queryable.ThenBy)
                    or nameof(Queryable.ThenByDescending)
                    or nameof(Queryable.Take)
                    or nameof(Queryable.First)
                    or nameof(Queryable.FirstOrDefault)
                    or nameof(Queryable.Single)
                    or nameof(Queryable.SingleOrDefault)
                    or nameof(EntityFrameworkQueryableExtensions.AsNoTracking)
                    or nameof(EntityFrameworkQueryableExtensions.AsTracking)
                    or nameof(EntityFrameworkQueryableExtensions.TagWith)
                    or nameof(EntityFrameworkQueryableExtensions.FirstAsync)
                    or nameof(EntityFrameworkQueryableExtensions.FirstOrDefaultAsync)
                    or nameof(EntityFrameworkQueryableExtensions.SingleAsync)
                    or nameof(EntityFrameworkQueryableExtensions.SingleOrDefaultAsync);
                if (!isQueryable || !allowed)
                {
                    throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                        $"the query operator '{node.Method.Name}' is outside the admitted root lock scope");
                }
            }

            for (var index = 1; index < node.Arguments.Count; index++)
            {
                if (MarkerCollector.Collect(node.Arguments[index]).Count > 0)
                {
                    throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                        "a row-lock request cannot appear inside a projection, join, or subquery argument");
                }
            }

            return base.VisitMethodCall(node);
        }
    }

    private sealed class ReservedTagValidator : ExpressionVisitor
    {
        private readonly bool _rejectDynamicTags;
        public ReservedTagValidator(bool rejectDynamicTags) => _rejectDynamicTags = rejectDynamicTags;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                && node.Method.Name == nameof(EntityFrameworkQueryableExtensions.TagWith)
                && node.Arguments.Count == 2)
            {
                if (node.Arguments[1] is not ConstantExpression { Value: string tag })
                {
                    if (_rejectDynamicTags)
                    {
                        throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                            "query tags beside a lock request must be constant strings");
                    }
                }
                else if (LockProtocol.ContainsReservedProtocolText(tag))
                {
                    throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                        "a user query tag spoofs the reserved lock protocol");
                }
            }

            return base.VisitMethodCall(node);
        }
    }

    private sealed class ExpressionTagRewriter : ExpressionVisitor
    {
        private readonly LockRequest _request;
        public ExpressionTagRewriter(LockRequest request) => _request = request;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (!RowLockExtensions.IsMarker(node.Method))
            {
                return base.VisitMethodCall(node);
            }

            var source = Visit(node.Arguments[0]);
            return Expression.Call(null, TagWithMethod.MakeGenericMethod(node.Method.GetGenericArguments()[0]),
                source, Expression.Constant(_request.Tag(PocCarrier.ExpressionTag)));
        }
    }
}
#pragma warning restore EF1001
