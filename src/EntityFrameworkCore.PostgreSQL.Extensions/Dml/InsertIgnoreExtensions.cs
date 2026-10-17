using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

/// <summary>Executes a narrow metadata-aware PostgreSQL insert that ignores one declared unique target.</summary>
public static class InsertIgnoreExtensions
{
    /// <summary>
    /// Inserts one entity row and returns whether PostgreSQL inserted it. Only conflicts on the selected mapped,
    /// unfiltered unique key are ignored; other constraint failures propagate.
    /// </summary>
    /// <typeparam name="TEntity">The mapped entity type.</typeparam>
    /// <param name="context">The context whose provider, connection and current transaction are used.</param>
    /// <param name="entity">The typed values to insert.</param>
    /// <param name="conflictTarget">A property or anonymous composite property key backed by a mapped unique key.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns><see langword="true"/> when one row was inserted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="NotSupportedException">The entity mapping or conflict target is outside the supported shape.</exception>
    public static async Task<bool> InsertIgnoreAsync<TEntity>(
        this DbContext context,
        TEntity entity,
        Expression<Func<TEntity, object?>> conflictTarget,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(conflictTarget);

        var connection = PostgreSqlCommandGuard.GetConnection(context.Database, nameof(InsertIgnoreAsync));
        var currentTransaction = PostgreSqlCommandGuard.GetCurrentTransaction(
            context.Database,
            connection,
            nameof(InsertIgnoreAsync),
            required: false);

        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new NotSupportedException($"Entity type {typeof(TEntity).Name} is not part of this EF model.");
        var mapping = ResolveMapping(entityType);
        var targetProperties = ReadConflictProperties(conflictTarget, entityType);
        ValidateConflictTarget(entityType, mapping.StoreObject, targetProperties);

        var values = BuildInsertValues(entityType, mapping.StoreObject, entity);
        var tableSql = mapping.Schema is null
            ? QuoteIdentifier(mapping.Table)
            : $"{QuoteIdentifier(mapping.Schema)}.{QuoteIdentifier(mapping.Table)}";
        var columnSql = values.Count == 0
            ? string.Empty
            : $" ({string.Join(", ", values.Select(value => QuoteIdentifier(value.ColumnName)))})";
        var parameterSql = values.Count == 0
            ? string.Empty
            : $" VALUES ({string.Join(", ", values.Select((_, index) => $"@p{index}"))})";
        var conflictSql = string.Join(", ", targetProperties.Select(property =>
        {
            var column = property.GetColumnName(mapping.StoreObject)
                ?? throw new NotSupportedException($"Property {property.Name} has no column in the mapped table.");
            return QuoteIdentifier(column);
        }));
        var sql = values.Count == 0
            ? $"INSERT INTO {tableSql} DEFAULT VALUES ON CONFLICT ({conflictSql}) DO NOTHING"
            : $"INSERT INTO {tableSql}{columnSql}{parameterSql} ON CONFLICT ({conflictSql}) DO NOTHING";

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = currentTransaction;
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var typeMapping = value.Property.GetRelationalTypeMapping();
            DbParameter parameter = typeMapping.CreateParameter(
                command,
                $"p{index}",
                value.Value,
                value.Property.IsNullable,
                ParameterDirection.Input);
            command.Parameters.Add(parameter);
        }

        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return affected switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidOperationException($"InsertIgnoreAsync expected an affected-row count of zero or one, but PostgreSQL reported {affected}.")
            };
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static TableMapping ResolveMapping(IEntityType entityType)
    {
        if (entityType.FindOwnership() is not null)
        {
            throw new NotSupportedException("InsertIgnoreAsync does not support owned entity mappings.");
        }

        if (entityType.BaseType is not null || entityType.GetDerivedTypes().Any())
        {
            throw new NotSupportedException("InsertIgnoreAsync does not support entity inheritance mappings.");
        }

        if (entityType.GetComplexProperties().Any())
        {
            throw new NotSupportedException("InsertIgnoreAsync does not support complex-property mappings.");
        }

        if (entityType.GetMappingFragments(StoreObjectType.Table).Any()
            || entityType.GetMappingFragments(StoreObjectType.View).Any())
        {
            throw new NotSupportedException("InsertIgnoreAsync requires one table mapping without table fragments.");
        }

        if (entityType.GetProperties().Any(property => property.IsShadowProperty()))
        {
            throw new NotSupportedException("InsertIgnoreAsync does not support shadow properties.");
        }

        var table = entityType.GetTableName();
        if (table is null || entityType.GetViewName() is not null || entityType.GetViewSchema() is not null)
        {
            throw new NotSupportedException("InsertIgnoreAsync requires one mapped table.");
        }

        var schema = entityType.GetSchema();
        if (entityType.Model.GetEntityTypes().Any(other =>
                !ReferenceEquals(other, entityType)
                && string.Equals(other.GetTableName(), table, StringComparison.Ordinal)
                && string.Equals(other.GetSchema(), schema, StringComparison.Ordinal)))
        {
            throw new NotSupportedException("InsertIgnoreAsync does not support entity types that share a table mapping.");
        }

        return new TableMapping(table, schema, StoreObjectIdentifier.Table(table, schema));
    }

    private static IReadOnlyList<IProperty> ReadConflictProperties<TEntity>(
        Expression<Func<TEntity, object?>> expression,
        IEntityType entityType)
    {
        var body = StripConvert(expression.Body);
        var members = body switch
        {
            MemberExpression member => new[] { member },
            NewExpression creation when IsAnonymousType(creation.Type)
                && creation.Members is { Count: > 0 } selectedMembers
                && selectedMembers.Count == creation.Arguments.Count
                => creation.Arguments.Select(argument => StripConvert(argument) as MemberExpression
                    ?? throw new NotSupportedException("Conflict targets must select mapped entity properties directly.")).ToArray(),
            _ => throw new NotSupportedException("Conflict targets must select one property or an anonymous composite key.")
        };

        var properties = new List<IProperty>(members.Length);
        foreach (var member in members)
        {
            if (member.Expression != expression.Parameters[0])
            {
                throw new NotSupportedException("Conflict targets must select properties directly from the entity parameter.");
            }

            var property = entityType.FindProperty(member.Member.Name)
                ?? throw new NotSupportedException($"Conflict target member {member.Member.Name} is not a mapped scalar property.");
            if (properties.Contains(property))
            {
                throw new NotSupportedException("A conflict target cannot contain the same property more than once.");
            }

            properties.Add(property);
        }

        return properties;
    }

    private static void ValidateConflictTarget(
        IEntityType entityType,
        StoreObjectIdentifier storeObject,
        IReadOnlyList<IProperty> targetProperties)
    {
        if (targetProperties.Count == 0 || targetProperties.Any(property => property.GetColumnName(storeObject) is null))
        {
            throw new NotSupportedException("Every conflict target property must map to a column in the target table.");
        }

        var keyMatches = entityType.GetKeys().Any(key => SameProperties(key.Properties, targetProperties));
        var indexMatches = entityType.GetIndexes().Any(index =>
            index.IsUnique
            && index.FindAnnotation("Npgsql:IndexExpression") is null
            && index.GetFilter(storeObject) is null
            && SameProperties(index.Properties, targetProperties));
        if (!keyMatches && !indexMatches)
        {
            var filteredMatch = entityType.GetIndexes().Any(index =>
                index.IsUnique
                && index.FindAnnotation("Npgsql:IndexExpression") is null
                && index.GetFilter(storeObject) is not null
                && SameProperties(index.Properties, targetProperties));
            throw new NotSupportedException(filteredMatch
                ? "Partial unique indexes are not supported as insert-ignore conflict targets."
                : "The selected properties are not a mapped unfiltered unique key; expression indexes are not supported.");
        }
    }

    private static List<InsertValue> BuildInsertValues(
        IEntityType entityType,
        StoreObjectIdentifier storeObject,
        object entity)
    {
        var values = new List<InsertValue>();
        foreach (var property in entityType.GetProperties())
        {
            var column = property.GetColumnName(storeObject)
                ?? throw new NotSupportedException($"Property {property.Name} has no column in the mapped table.");
            var value = property.GetGetter().GetClrValue(entity);
            var generated = IsDatabaseGenerated(property, storeObject);

            if (property.ValueGenerated == ValueGenerated.OnAddOrUpdate)
            {
                if (!generated)
                {
                    throw new NotSupportedException($"Client-generated property {property.Name} requires SaveChanges and is not supported.");
                }

                if (IsSentinel(property, value))
                {
                    continue;
                }

                throw new NotSupportedException($"Explicit values for database-generated property {property.Name} are not supported.");
            }

            if (generated && IsSentinel(property, value))
            {
                continue;
            }

            if (property.ValueGenerated == ValueGenerated.OnAdd && !generated)
            {
                throw new NotSupportedException($"Client-generated property {property.Name} requires SaveChanges and is not supported.");
            }

            var strategy = property.GetValueGenerationStrategy(storeObject);
            if (strategy == NpgsqlValueGenerationStrategy.IdentityAlwaysColumn && !IsSentinel(property, value))
            {
                throw new NotSupportedException($"Explicit values for identity-always property {property.Name} are not supported.");
            }

            values.Add(new InsertValue(property, column, value));
        }

        return values;
    }

    private static bool IsDatabaseGenerated(IProperty property, StoreObjectIdentifier storeObject)
    {
        var strategy = property.GetValueGenerationStrategy(storeObject);
        return strategy is NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
                or NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                or NpgsqlValueGenerationStrategy.SerialColumn
            || property.FindAnnotation("Relational:DefaultValue") is not null
            || property.GetDefaultValueSql(storeObject) is not null
            || property.GetComputedColumnSql(storeObject) is not null;
    }

    private static bool IsSentinel(IProperty property, object? value)
    {
        var sentinel = property.Sentinel;
        var comparer = property.GetValueComparer();
        return comparer is null ? Equals(value, sentinel) : comparer.Equals(value, sentinel);
    }

    private static bool SameProperties(IReadOnlyList<IProperty> left, IReadOnlyList<IProperty> right)
        => left.Count == right.Count && left.All(right.Contains);

    private static bool IsAnonymousType(Type type)
        => type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            && type.IsGenericType
            && type.Name.Contains("AnonymousType", StringComparison.Ordinal);

    private static string QuoteIdentifier(string identifier)
        => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private sealed record TableMapping(string Table, string? Schema, StoreObjectIdentifier StoreObject);

    private sealed record InsertValue(IProperty Property, string ColumnName, object? Value);
}
