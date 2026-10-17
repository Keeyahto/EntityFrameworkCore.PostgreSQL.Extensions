using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

// EF1001 is scoped to this file because the carrier comparison must derive from the pinned Npgsql generator hooks.
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

        var requests = selectExpression.Tags
            .Where(tag => tag.StartsWith(LockProtocol.TagPrefix, StringComparison.Ordinal))
            .ToArray();
        if (requests.Length == 0)
        {
            return;
        }

        if (requests.Length != 1 || !TryParse(requests[0], out var carrier, out var request))
        {
            throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected("the SELECT contains invalid or conflicting lock tags");
        }

        if (selectExpression.IsDistinct
            || selectExpression.GroupBy.Count != 0
            || selectExpression.Having is not null
            || selectExpression.Tables.Count != 1
            || selectExpression.Tables[0] is not TableExpression target)
        {
            throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                "the final SELECT no longer identifies one simple root table occurrence");
        }

        var annotation = target.FindAnnotation(LockProtocol.AnnotationName);
        if ((carrier == PocCarrier.TableAnnotations
                && (annotation?.Value is not LockRequest annotatedRequest || annotatedRequest != request))
            || (carrier != PocCarrier.TableAnnotations && annotation is not null))
        {
            throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected(
                "the table occurrence carrier does not match the lock request");
        }

        Sql.Append(" FOR ");
        Sql.Append(request.Strength switch
        {
            LockStrength.Update => "UPDATE",
            LockStrength.NoKeyUpdate => "NO KEY UPDATE",
            LockStrength.Share => "SHARE",
            LockStrength.KeyShare => "KEY SHARE",
            _ => throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected("the lock strength is invalid")
        });
        Sql.Append(" OF ");
        Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(target.Alias));
        switch (request.Wait)
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
                throw RowLockQueryableMethodTranslatingExpressionVisitor.Rejected("the lock wait mode is invalid");
        }

        Sql.Append(" /*");
        Sql.Append(request.Receipt(carrier));
        Sql.Append("*/");
    }

    private static bool TryParse(string tag, out PocCarrier carrier, out LockRequest request)
    {
        carrier = default;
        request = default;
        var parts = tag[LockProtocol.TagPrefix.Length..].Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var carrierValue)
            || !Enum.IsDefined((PocCarrier)carrierValue)
            || !int.TryParse(parts[1], out var strengthValue)
            || !Enum.IsDefined((LockStrength)strengthValue)
            || !int.TryParse(parts[2], out var waitValue)
            || !Enum.IsDefined((LockWait)waitValue))
        {
            return false;
        }

        carrier = (PocCarrier)carrierValue;
        request = new LockRequest((LockStrength)strengthValue, (LockWait)waitValue);
        return true;
    }
}
#pragma warning restore EF1001
