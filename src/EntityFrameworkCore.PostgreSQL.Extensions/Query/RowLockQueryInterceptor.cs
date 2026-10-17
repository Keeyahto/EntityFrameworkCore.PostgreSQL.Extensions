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

// EF1001 is scoped to this file for exact pinned Npgsql service-identity validation at execution.
#pragma warning disable EF1001
internal sealed class RowLockQueryInterceptor : DbCommandInterceptor, IQueryExpressionInterceptor
{
    private static readonly MethodInfo TagWithMethod = typeof(EntityFrameworkQueryableExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(EntityFrameworkQueryableExtensions.TagWith)
            && method.IsGenericMethodDefinition
            && method.GetParameters().Length == 2
            && method.GetParameters()[1].ParameterType == typeof(string));

    public static RowLockQueryInterceptor Instance { get; } = new();

    private RowLockQueryInterceptor()
    {
    }

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        var context = eventData.Context
            ?? throw RowLockQueryErrors.Rejected("the query compilation context is unavailable");
        ValidateServices(context);
        ReservedProtocolTagValidator.Validate(queryExpression);

        var markers = MarkerCollector.Collect(queryExpression);
        if (markers.Count == 0)
        {
            return queryExpression;
        }

        if (markers.Count != 1)
        {
            throw RowLockQueryErrors.Rejected("a query may contain only one row-lock request");
        }

        var marker = markers[0];
        var intent = RowLockQueryValidation.Validate(queryExpression, marker.Request, context);
        return new ExpressionTagRewriter(intent).Visit(queryExpression)!;
    }

    private void BeforeCommand(DbCommand command, CommandEventData eventData)
    {
        var requests = FindProtocolValues(command.CommandText, LockProtocol.TagPrefix, "\n");
        var receipts = FindProtocolValues(command.CommandText, LockProtocol.ReceiptPrefix, "*/");
        if (requests.Count == 0 && receipts.Count == 0)
        {
            return;
        }

        if (requests.Count != 1 || receipts.Count != 1
            || !LockProtocol.TryParseIntent(requests[0], LockProtocol.TagPrefix, out var intent)
            || !LockProtocol.TryParseIntent(receipts[0], LockProtocol.ReceiptPrefix, out var receipt)
            || intent != receipt)
        {
            throw RowLockQueryErrors.Rejected(
                "the generated command does not contain one matching lock request and fulfillment receipt");
        }

        var context = eventData.Context
            ?? throw RowLockQueryErrors.Rejected("the execution context is unavailable");
        ValidateServices(context);
        if (intent.TakeParameterName is not null)
        {
            var takeParameter = command.Parameters.Cast<DbParameter>()
                .SingleOrDefault(parameter => string.Equals(
                    parameter.ParameterName.TrimStart('@', ':'), intent.TakeParameterName, StringComparison.Ordinal));
            if (takeParameter?.Value is not int takeValue || takeValue != 1)
            {
                throw RowLockQueryErrors.Rejected("the exact Take(1) query parameter must have value 1 at execution");
            }
        }

        var current = context.Database.CurrentTransaction?.GetDbTransaction();
        if (current is null
            || !ReferenceEquals(current, command.Transaction)
            || !ReferenceEquals(current.Connection, command.Connection))
        {
            throw RowLockQueryErrors.Rejected(
                "execution requires the current borrowed EF transaction on the command connection");
        }
    }

    private static void ValidateServices(DbContext context)
    {
        var generatorFactory = context.GetService<IQuerySqlGeneratorFactory>();
        var translatorFactory = context.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>();
        if (generatorFactory.GetType() != typeof(RowLockQuerySqlGeneratorFactory)
            || translatorFactory.GetType() != typeof(NpgsqlQueryableMethodTranslatingExpressionVisitorFactory))
        {
            throw RowLockQueryErrors.Rejected(
                "the configured query generator or native Npgsql translator service was replaced");
        }
    }

    private static List<string> FindProtocolValues(string commandText, string prefix, string terminator)
    {
        var values = new List<string>();
        var offset = 0;
        while ((offset = commandText.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var start = offset + prefix.Length;
            var end = commandText.IndexOf(terminator, start, StringComparison.Ordinal);
            if (end < 0)
            {
                values.Add(string.Empty);
                break;
            }

            values.Add(commandText[start..end].Trim());
            offset = end + terminator.Length;
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
            if (RowLockExtensions.TryRead(node, out var request))
            {
                _markers.Add(new FoundMarker(request));
            }

            return base.VisitMethodCall(node);
        }
    }

    private readonly record struct FoundMarker(LockRequest Request);

    private sealed class ReservedProtocolTagValidator : ExpressionVisitor
    {
        public static void Validate(Expression expression)
            => new ReservedProtocolTagValidator().Visit(expression);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsGenericMethod
                && node.Method.GetGenericMethodDefinition() == TagWithMethod
                && node.Arguments[1] is ConstantExpression { Value: string tag }
                && LockProtocol.ContainsReservedProtocolText(tag))
            {
                throw RowLockQueryErrors.Rejected("a user query tag spoofs the reserved lock protocol");
            }

            return base.VisitMethodCall(node);
        }
    }

    private sealed class ExpressionTagRewriter : ExpressionVisitor
    {
        private readonly LockQueryIntent _intent;

        public ExpressionTagRewriter(LockQueryIntent intent) => _intent = intent;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (!RowLockExtensions.IsMarker(node.Method))
            {
                return base.VisitMethodCall(node);
            }

            var source = Visit(node.Arguments[0]);
            return Expression.Call(null,
                TagWithMethod.MakeGenericMethod(node.Method.GetGenericArguments()[0]),
                source,
                Expression.Constant(_intent.Tag));
        }
    }
}
#pragma warning restore EF1001
