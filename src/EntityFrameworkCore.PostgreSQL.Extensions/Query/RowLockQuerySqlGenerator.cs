using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

// EF1001 is scoped to this file because the implementation extends the pinned Npgsql generator hooks.
#pragma warning disable EF1001
internal sealed class RowLockQuerySqlGeneratorFactory : NpgsqlQuerySqlGeneratorFactory
{
    private readonly QuerySqlGeneratorDependencies _dependencies;
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly INpgsqlSingletonOptions _npgsqlSingletonOptions;

    public RowLockQuerySqlGeneratorFactory(
        QuerySqlGeneratorDependencies dependencies,
        IRelationalTypeMappingSource typeMappingSource,
        INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies, typeMappingSource, npgsqlSingletonOptions)
    {
        _dependencies = dependencies;
        _typeMappingSource = typeMappingSource;
        _npgsqlSingletonOptions = npgsqlSingletonOptions;
    }

    public override QuerySqlGenerator Create()
        => new RowLockQuerySqlGenerator(
            _dependencies,
            _typeMappingSource,
            _npgsqlSingletonOptions.ReverseNullOrderingEnabled,
            _npgsqlSingletonOptions.PostgresVersion);
}

internal sealed class RowLockQuerySqlGenerator : NpgsqlQuerySqlGenerator
{
    public RowLockQuerySqlGenerator(
        QuerySqlGeneratorDependencies dependencies,
        IRelationalTypeMappingSource typeMappingSource,
        bool reverseNullOrdering,
        Version postgresVersion)
        : base(dependencies, typeMappingSource, reverseNullOrdering, postgresVersion)
    {
    }

    protected override void GenerateLimitOffset(SelectExpression selectExpression)
    {
        base.GenerateLimitOffset(selectExpression);

        var tags = selectExpression.Tags
            .Where(tag => tag.StartsWith(LockProtocol.TagPrefix, StringComparison.Ordinal))
            .ToArray();
        if (tags.Length == 0)
        {
            return;
        }

        if (tags.Length != 1
            || !LockProtocol.TryParseIntent(tags[0][LockProtocol.TagPrefix.Length..], LockProtocol.TagPrefix, out var intent))
        {
            throw RowLockQueryErrors.Rejected("the SELECT contains invalid or conflicting lock intent tags");
        }

        if (selectExpression.IsDistinct
            || selectExpression.GroupBy.Count != 0
            || selectExpression.Having is not null
            || selectExpression.Offset is not null
            || selectExpression.Projection.Count == 0
            || selectExpression.Tables.Count != 1
            || selectExpression.Tables[0] is not TableExpression target
            || string.IsNullOrEmpty(target.Alias)
            || !string.Equals(target.Table.Name, intent.TableName, StringComparison.Ordinal)
            || !string.Equals(target.Table.Schema, intent.Schema, StringComparison.Ordinal)
            || target.Table.EntityTypeMappings.Count() != 1
            || target.Table.EntityTypeMappings.Single().TypeBase.Name != intent.EntityTypeName)
        {
            throw RowLockQueryErrors.Rejected(
                "the finalized SELECT must retain one exact mapped table occurrence and a stable lock scope");
        }

        if (intent.IsOrderedSet)
        {
            ValidateOrderedSetSelect(selectExpression, target, intent);
        }
        else if (!LimitIsAdmitted(selectExpression.Limit, intent))
        {
            throw RowLockQueryErrors.Rejected("the finalized SELECT does not preserve its exact Take(1) or approved terminal limit");
        }

        var treeGuard = new FinalSelectGuard(target.Alias, intent.IsOrderedSet ? intent.OrderedSetKeyColumn : null);
        foreach (var projection in selectExpression.Projection)
        {
            treeGuard.Visit(projection.Expression);
        }

        if (selectExpression.Predicate is not null)
        {
            treeGuard.Visit(selectExpression.Predicate);
        }

        foreach (var ordering in selectExpression.Orderings)
        {
            treeGuard.Visit(ordering.Expression);
        }

        if (selectExpression.Limit is not null)
        {
            treeGuard.Visit(selectExpression.Limit);
        }

        if (intent.IsOrderedSet && treeGuard.UuidMembershipCount != 1)
        {
            throw RowLockQueryErrors.Rejected("the finalized SELECT must retain exactly one native Guid-array membership predicate on its lock key");
        }

        Sql.Append(" FOR ");
        Sql.Append(intent.Strength switch
        {
            LockStrength.Update => "UPDATE",
            LockStrength.NoKeyUpdate => "NO KEY UPDATE",
            LockStrength.Share => "SHARE",
            LockStrength.KeyShare => "KEY SHARE",
            _ => throw RowLockQueryErrors.Rejected("the lock strength is invalid")
        });
        Sql.Append(" OF ");
        Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(target.Alias));
        switch (intent.Wait)
        {
            case LockWait.NoWait:
                Sql.Append(" NOWAIT");
                break;
            case LockWait.SkipLocked:
                Sql.Append(" SKIP LOCKED");
                break;
            case LockWait.Wait:
                break;
            default:
                throw RowLockQueryErrors.Rejected("the lock wait mode is invalid");
        }

        Sql.Append(" /*");
        Sql.Append(intent.Receipt);
        Sql.Append("*/");
    }

    private static void ValidateOrderedSetSelect(SelectExpression selectExpression, TableExpression target, LockQueryIntent intent)
    {
        if (selectExpression.Limit is not null || selectExpression.Orderings.Count != (intent.OrderedSetPrefixColumn is null ? 1 : 2))
        {
            throw RowLockQueryErrors.Rejected("the finalized UUID-set SELECT must enumerate every match with no limit and the original ordering terms");
        }

        var expectedColumns = intent.OrderedSetPrefixColumn is null
            ? new[] { intent.OrderedSetKeyColumn! }
            : new[] { intent.OrderedSetPrefixColumn, intent.OrderedSetKeyColumn! };
        for (var index = 0; index < expectedColumns.Length; index++)
        {
            var ordering = selectExpression.Orderings[index];
            if (!ordering.IsAscending
                || ordering.Expression is not ColumnExpression column
                || column.TableAlias != target.Alias
                || column.Name != expectedColumns[index])
            {
                throw RowLockQueryErrors.Rejected("the finalized UUID-set SELECT must preserve the original ascending root-column ordering");
            }
        }
    }

    private static bool LimitIsAdmitted(SqlExpression? limit, LockQueryIntent intent)
    {
        if (!intent.HasTake)
        {
            return limit is null || IsIntegerConstant(limit, 1) || IsIntegerConstant(limit, 2);
        }

        if (limit is null)
        {
            return false;
        }

        if (intent.TakeParameterName is not null)
        {
            if (limit is SqlParameterExpression parameter
                && parameter.Name == intent.TakeParameterName)
            {
                return true;
            }

            return IsLeastWithTake(limit, expression => expression is SqlParameterExpression value
                    && value.Name == intent.TakeParameterName,
                expression => IsIntegerConstant(expression, 1) || IsIntegerConstant(expression, 2));
        }

        return IsIntegerConstant(limit, 1)
            || IsLeastWithTake(limit,
                expression => IsIntegerConstant(expression, 1),
                expression => IsIntegerConstant(expression, 1) || IsIntegerConstant(expression, 2));
    }

    private static bool IsLeastWithTake(
        SqlExpression expression,
        Func<SqlExpression, bool> take,
        Func<SqlExpression, bool> terminalLimit)
        => expression is SqlFunctionExpression { Name: "LEAST", Arguments.Count: 2 } least
            && (take(least.Arguments[0]) && terminalLimit(least.Arguments[1])
                || take(least.Arguments[1]) && terminalLimit(least.Arguments[0]));

    private static bool IsIntegerConstant(SqlExpression expression, int expected)
        => expression is SqlConstantExpression { Value: int value } && value == expected;

    private sealed class FinalSelectGuard : ExpressionVisitor
    {
        private readonly string _targetAlias;
        private readonly string? _uuidMembershipColumn;
        public int UuidMembershipCount { get; private set; }

        public FinalSelectGuard(string targetAlias, string? uuidMembershipColumn)
        {
            _targetAlias = targetAlias;
            _uuidMembershipColumn = uuidMembershipColumn;
        }

        protected override Expression VisitExtension(Expression node)
        {
            if (node is SelectExpression
                or ExistsExpression
                or ScalarSubqueryExpression
                || node is InExpression { Subquery: not null })
            {
                throw RowLockQueryErrors.Rejected("nested relational SELECT constructs are outside the final lock scope");
            }

            if (node is ColumnExpression column && column.TableAlias != _targetAlias)
            {
                throw RowLockQueryErrors.Rejected("the final SELECT contains a column outside its exact root table alias");
            }

            if (node is PgAnyExpression any)
            {
                UuidMembershipCount++;
                if (_uuidMembershipColumn is null
                    || any.OperatorType != PgAnyOperatorType.Equal
                    || any.Item is not ColumnExpression { } item
                    || item.TableAlias != _targetAlias
                    || item.Name != _uuidMembershipColumn
                    || any.Array.Type != typeof(Guid[]))
                {
                    throw RowLockQueryErrors.Rejected("the finalized SELECT contains an unadmitted PostgreSQL array membership expression");
                }
            }

            return base.VisitExtension(node);
        }
    }
}
#pragma warning restore EF1001
