using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

internal static class RowLockQueryErrors
{
    public static InvalidOperationException Rejected(string reason)
        => new($"The PostgreSQL row-lock request cannot be fulfilled: {reason}.");
}

internal sealed class RowLockQueryValidation
{
    private static readonly HashSet<MethodInfo> QueryMethods = CreateQueryMethods();
    private static readonly MethodInfo EnumerableContainsMethod = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Enumerable.Contains)
            && method.IsGenericMethodDefinition
            && method.GetGenericArguments().Length == 1
            && method.GetParameters().Length == 2
            && method.GetParameters()[0].ParameterType.IsGenericType
            && method.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>));
    private readonly List<LambdaExpression> _predicates = new();
    private readonly List<LambdaExpression> _orderings = new();
    private readonly List<string> _orderingOperators = new();
    private readonly List<string> _outerToInnerOperators = new();
    private int _takeCount;
    private int _terminalCount;
    private int _uuidMembershipCount;
    private string? _takeParameterName;
    private bool _hasTake;
    private string? _orderedSetPrefixColumn;
    private string? _orderedSetKeyColumn;

    public static LockQueryIntent Validate(Expression expression, LockRequest request, DbContext context)
    {
        var validation = new RowLockQueryValidation();
        var root = validation.ReadChain(expression);
        var entityType = root.EntityType;
        validation.ValidateModel(entityType, context.Model);
        foreach (var predicate in validation._predicates)
        {
            validation.ValidatePredicate(predicate, entityType);
        }

        foreach (var ordering in validation._orderings)
        {
            validation.ValidateOrdering(ordering, entityType);
        }

        if (validation._uuidMembershipCount > 1)
        {
            throw RowLockQueryErrors.Rejected("only one UUID-array membership predicate is supported");
        }

        if (validation._uuidMembershipCount == 1)
        {
            validation.ValidateOrderedSet(request, entityType);
        }
        else
        {
            validation.ValidateOrderingAndTakePlacement();
            if (validation._orderings.Count > 0 && validation._takeCount != 1)
            {
                throw RowLockQueryErrors.Rejected("ordering is supported only with one Take(1)");
            }
        }

        var tableName = entityType.GetTableName()
            ?? throw RowLockQueryErrors.Rejected("the root entity has no single mapped table");
        return new LockQueryIntent(
            request.Strength,
            request.Wait,
            entityType.Name,
            tableName,
            entityType.GetSchema(),
            validation._takeParameterName,
            validation._hasTake,
            validation._orderedSetPrefixColumn,
            validation._orderedSetKeyColumn);
    }

    private EntityQueryRootExpression ReadChain(Expression expression)
    {
        if (expression.GetType() == typeof(EntityQueryRootExpression)
            && expression is EntityQueryRootExpression root)
        {
            return root;
        }

        if (expression is not MethodCallExpression call)
        {
            throw RowLockQueryErrors.Rejected("the query must be a direct mapped entity-root chain");
        }

        if (RowLockExtensions.TryRead(call, out _))
        {
            return ReadChain(call.Arguments[0]);
        }

        if (!QueryMethods.Contains(call.Method.IsGenericMethod ? call.Method.GetGenericMethodDefinition() : call.Method))
        {
            throw RowLockQueryErrors.Rejected($"the query operator '{call.Method.Name}' is outside the admitted root lock grammar");
        }

        var declaringType = call.Method.DeclaringType;
        var name = call.Method.Name;
        var args = call.Arguments;
        _outerToInnerOperators.Add(name);
        if (declaringType == typeof(Queryable) && name == nameof(Queryable.Where)
            || declaringType == typeof(Queryable) && IsQueryablePredicateTerminal(name, args.Count)
            || declaringType == typeof(EntityFrameworkQueryableExtensions) && IsPredicateTerminal(name, args.Count))
        {
            _predicates.Add(ReadLambda(args[1]));
            if (IsPredicateTerminal(name, args.Count) || IsQueryablePredicateTerminal(name, args.Count))
            {
                _terminalCount++;
                if (_terminalCount > 1)
                {
                    throw RowLockQueryErrors.Rejected("only one row-lock terminal is supported");
                }
            }
        }
        else if (declaringType == typeof(Queryable) && IsOrdering(name))
        {
            _orderings.Add(ReadLambda(args[1]));
            _orderingOperators.Add(name);
        }
        else if (declaringType == typeof(Queryable) && name == nameof(Queryable.Take))
        {
            _takeCount++;
            _hasTake = true;
            if (_takeCount != 1)
            {
                throw RowLockQueryErrors.Rejected("only one Take(1) is supported");
            }

            switch (args[1])
            {
                case ConstantExpression { Value: 1 }:
                    break;
                case QueryParameterExpression parameter when parameter.Type == typeof(int):
                    _takeParameterName = parameter.Name;
                    break;
                default:
                    throw RowLockQueryErrors.Rejected("only a structurally exact Take(1) is supported");
            }
        }
        else if (IsTerminal(name, args.Count))
        {
            _terminalCount++;
            if (_terminalCount > 1)
            {
                throw RowLockQueryErrors.Rejected("only one row-lock terminal is supported");
            }
        }
        else if (declaringType == typeof(EntityFrameworkQueryableExtensions) && name == nameof(EntityFrameworkQueryableExtensions.TagWith))
        {
            if (args[1] is not ConstantExpression { Value: string tag })
            {
                throw RowLockQueryErrors.Rejected("query tags beside a lock request must be constant strings");
            }

            if (LockProtocol.ContainsReservedProtocolText(tag))
            {
                throw RowLockQueryErrors.Rejected("a user query tag spoofs the reserved lock protocol");
            }
        }

        return ReadChain(args[0]);
    }

    private void ValidateOrderingAndTakePlacement()
    {
        var operators = _outerToInnerOperators.AsEnumerable().Reverse().ToArray();
        var orderings = operators.Where(IsOrdering).ToArray();
        if (orderings.Length == 0)
        {
            return;
        }

        if (_takeCount != 1
            || !IsPrimaryOrdering(orderings[0])
            || orderings.Skip(1).Any(IsPrimaryOrdering))
        {
            throw RowLockQueryErrors.Rejected("ordering requires one OrderBy, optional ThenBy keys, and one Take(1)");
        }

        var lastOrdering = Array.FindLastIndex(operators, IsOrdering);
        var take = Array.IndexOf(operators, nameof(Queryable.Take));
        if (take < 0 || take < lastOrdering)
        {
            throw RowLockQueryErrors.Rejected("the single Take(1) must follow the admitted ordering chain");
        }
    }

    private void ValidateOrderedSet(LockRequest request, IEntityType entityType)
    {
        if (request.Strength != LockStrength.Update || request.Wait != LockWait.Wait)
        {
            throw RowLockQueryErrors.Rejected("UUID-array locking requires Update with Wait");
        }

        if (_takeCount != 0 || _terminalCount != 0)
        {
            throw RowLockQueryErrors.Rejected("UUID-array locking requires complete entity enumeration without Take, First, or Single");
        }

        if (_orderings.Count is < 1 or > 2)
        {
            throw RowLockQueryErrors.Rejected("UUID-array locking requires ascending Id, optionally preceded by one ascending direct Guid key");
        }

        var operatorNames = _orderingOperators.AsEnumerable().Reverse().ToArray();
        if (operatorNames[0] != nameof(Queryable.OrderBy)
            || operatorNames.Skip(1).Any(name => name != nameof(Queryable.ThenBy)))
        {
            throw RowLockQueryErrors.Rejected("UUID-array ordering must use ascending OrderBy and optional ThenBy");
        }

        var orderedMembers = _orderings.AsEnumerable().Reverse()
            .Select(ordering => ReadOrderingProperty(ordering, entityType))
            .ToArray();
        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey is null || primaryKey.Properties.Count != 1)
        {
            throw RowLockQueryErrors.Rejected("UUID-array locking requires one single-property Guid primary key");
        }

        var key = primaryKey.Properties[0];
        if (!IsDirectUuidProperty(key)
            || !ReferenceEquals(orderedMembers[^1], key))
        {
            throw RowLockQueryErrors.Rejected("UUID-array membership and final ordering must use the direct unconverted Guid primary key");
        }

        if (orderedMembers.Length == 2)
        {
            var prefix = orderedMembers[0];
            if (!IsDirectUuidProperty(prefix) || ReferenceEquals(prefix, key))
            {
                throw RowLockQueryErrors.Rejected("the optional UUID-array ordering prefix must be a separate direct Guid property");
            }

            _orderedSetPrefixColumn = ReadColumnName(prefix, entityType);
        }

        _orderedSetKeyColumn = ReadColumnName(key, entityType);
    }

    private void ValidateUuidMembership(MethodCallExpression call, ParameterExpression parameter, IEntityType entityType)
    {
        if (!call.Method.IsGenericMethod
            || call.Method.GetGenericMethodDefinition() != EnumerableContainsMethod
            || call.Method.GetGenericArguments()[0] != typeof(Guid)
            || call.Arguments.Count != 2
            || call.Arguments[0].Type != typeof(Guid[])
            || !IsCapturedOrParameterizedGuidArray(call.Arguments[0])
            || call.Arguments[1] is not MemberExpression { Expression: ParameterExpression root } member
            || root != parameter
            || member.Member is not (PropertyInfo or FieldInfo)
            || entityType.FindProperty(member.Member.Name) is not IProperty property
            || property.PropertyInfo != member.Member && property.FieldInfo != member.Member
            || !IsDirectUuidProperty(property)
            || entityType.FindPrimaryKey() is not { Properties.Count: 1 } primaryKey
            || !ReferenceEquals(property, primaryKey.Properties[0]))
        {
            throw RowLockQueryErrors.Rejected("membership requires one captured or compiled Guid[] Enumerable.Contains<Guid> over the direct unconverted Guid primary key");
        }

        _uuidMembershipCount++;
    }

    private static bool IsGuidMemberContains(MethodCallExpression call, ParameterExpression parameter, IEntityType entityType)
        => call.Method.Name == nameof(Enumerable.Contains)
            && (call.Arguments.Any(argument => IsDirectGuidMember(argument, parameter, entityType))
                || call.Object is not null && IsDirectGuidMember(call.Object, parameter, entityType));

    private static bool IsDirectGuidMember(Expression expression, ParameterExpression parameter, IEntityType entityType)
        => expression is MemberExpression { Expression: ParameterExpression root } member
            && root == parameter
            && member.Member is (PropertyInfo or FieldInfo)
            && entityType.FindProperty(member.Member.Name) is IProperty property
            && property.ClrType == typeof(Guid)
            && (property.PropertyInfo == member.Member || property.FieldInfo == member.Member);

    private static bool IsCapturedOrParameterizedGuidArray(Expression expression)
        => expression is QueryParameterExpression { Type: var parameterType } && parameterType == typeof(Guid[])
            || expression is MemberExpression { Type: var memberType, Expression: ConstantExpression }
                && memberType == typeof(Guid[]);

    private static bool IsDirectUuidProperty(IProperty property)
        => property.ClrType == typeof(Guid)
            && property.GetTypeMapping().Converter is null
            && string.Equals(property.GetRelationalTypeMapping().StoreType, "uuid", StringComparison.OrdinalIgnoreCase);

    private static IProperty ReadOrderingProperty(LambdaExpression ordering, IEntityType entityType)
    {
        if (ordering.Parameters.Count != 1
            || ordering.Parameters[0].Type != entityType.ClrType
            || ordering.Body is not MemberExpression { Expression: ParameterExpression parameter } member
            || parameter != ordering.Parameters[0]
            || member.Member is not (PropertyInfo or FieldInfo)
            || entityType.FindProperty(member.Member.Name) is not IProperty property
            || property.PropertyInfo != member.Member && property.FieldInfo != member.Member)
        {
            throw RowLockQueryErrors.Rejected("ordering keys must be direct mapped scalar members of the root entity");
        }

        return property;
    }

    private static string ReadColumnName(IProperty property, IEntityType entityType)
    {
        var tableName = entityType.GetTableName()
            ?? throw RowLockQueryErrors.Rejected("the root entity has no single mapped table");
        var columnName = property.GetColumnName(StoreObjectIdentifier.Table(tableName, entityType.GetSchema()));
        return columnName
            ?? throw RowLockQueryErrors.Rejected("the ordered UUID property has no mapped table column");
    }

    private static bool IsPrimaryOrdering(string name)
        => name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending);

    private static bool IsPredicateTerminal(string name, int argumentCount)
        => (name is nameof(EntityFrameworkQueryableExtensions.FirstAsync)
            or nameof(EntityFrameworkQueryableExtensions.FirstOrDefaultAsync)
            or nameof(EntityFrameworkQueryableExtensions.SingleAsync)
            or nameof(EntityFrameworkQueryableExtensions.SingleOrDefaultAsync))
            && argumentCount == 3;

    private static bool IsQueryablePredicateTerminal(string name, int argumentCount)
        => (name is nameof(Queryable.First)
            or nameof(Queryable.FirstOrDefault)
            or nameof(Queryable.Single)
            or nameof(Queryable.SingleOrDefault))
            && argumentCount == 2;

    private static bool IsTerminal(string name, int argumentCount)
        => (name is nameof(Queryable.First)
            or nameof(Queryable.FirstOrDefault)
            or nameof(Queryable.Single)
            or nameof(Queryable.SingleOrDefault))
            && argumentCount == 1
            || (name is nameof(EntityFrameworkQueryableExtensions.FirstAsync)
                or nameof(EntityFrameworkQueryableExtensions.FirstOrDefaultAsync)
                or nameof(EntityFrameworkQueryableExtensions.SingleAsync)
                or nameof(EntityFrameworkQueryableExtensions.SingleOrDefaultAsync))
                && argumentCount == 2;

    private static bool IsOrdering(string name)
        => name is nameof(Queryable.OrderBy)
            or nameof(Queryable.OrderByDescending)
            or nameof(Queryable.ThenBy)
            or nameof(Queryable.ThenByDescending);

    private static LambdaExpression ReadLambda(Expression expression)
    {
        if (expression is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression quoted })
        {
            return quoted;
        }

        throw RowLockQueryErrors.Rejected("predicates and ordering keys must be direct expression lambdas");
    }

    private void ValidateModel(IEntityType entityType, IModel model)
    {
        if (model.FindEntityType(entityType.Name) is not IEntityType mappedEntity
            || !ReferenceEquals(entityType, mappedEntity)
            || mappedEntity.IsOwned()
            || mappedEntity.BaseType is not null
            || mappedEntity.GetDerivedTypes().Any()
            || mappedEntity.GetDeclaredQueryFilters().Count > 0
            || mappedEntity.GetNavigations().Any(navigation => navigation.IsEagerLoaded)
            || mappedEntity.GetSkipNavigations().Any(navigation => navigation.IsEagerLoaded)
            || mappedEntity.GetComplexProperties().Any()
            || mappedEntity.GetMappingFragments(StoreObjectType.Table).Any()
            || mappedEntity.GetViewName() is not null
            || mappedEntity.GetSqlQuery() is not null
            || mappedEntity.GetFunctionName() is not null
            || mappedEntity.GetTableName() is null
            || mappedEntity.GetTableMappings().Count() != 1)
        {
            throw RowLockQueryErrors.Rejected(
                "the entity mapping must be one unfiltered, non-inherited, non-fragmented table without eager navigations or complex properties");
        }

        var tableMapping = mappedEntity.GetTableMappings().Single();
        var entityMappings = tableMapping.Table.EntityTypeMappings;
        if (entityMappings.Count() != 1
            || !ReferenceEquals(entityMappings.Single().TypeBase, mappedEntity))
        {
            throw RowLockQueryErrors.Rejected("shared-table entity mappings are outside the admitted root lock scope");
        }
    }

    private void ValidatePredicate(LambdaExpression predicate, IEntityType entityType)
    {
        if (predicate.Parameters.Count != 1 || predicate.Parameters[0].Type != entityType.ClrType)
        {
            throw RowLockQueryErrors.Rejected("a predicate must target one direct root entity parameter");
        }

        ValidatePredicateNode(predicate.Body, predicate.Parameters[0], entityType);
    }

    private void ValidatePredicateNode(Expression expression, ParameterExpression parameter, IEntityType entityType)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } conjunction)
        {
            ValidatePredicateNode(conjunction.Left, parameter, entityType);
            ValidatePredicateNode(conjunction.Right, parameter, entityType);
            return;
        }

        if (expression is MethodCallExpression methodCall)
        {
            if (IsGuidMemberContains(methodCall, parameter, entityType))
            {
                ValidateUuidMembership(methodCall, parameter, entityType);
                return;
            }

            throw RowLockQueryErrors.Rejected("predicates may contain only direct mapped scalar equality and AND");
        }

        if (expression is not BinaryExpression { NodeType: ExpressionType.Equal } equality)
        {
            throw RowLockQueryErrors.Rejected("predicates may contain only direct mapped scalar equality and AND");
        }

        var leftIsMember = TryReadPredicateMember(equality.Left, parameter, out var member, out var promotedShort);
        var allowPromotedShortValue = promotedShort && equality.Method is null;
        Expression value = equality.Right;
        if (!leftIsMember || !IsSupportedValue(value, allowPromotedShortValue))
        {
            if (!TryReadPredicateMember(equality.Right, parameter, out member, out promotedShort))
            {
                throw RowLockQueryErrors.Rejected("equality must compare a direct root scalar member with a constant or query parameter");
            }

            value = equality.Left;
            allowPromotedShortValue = promotedShort && equality.Method is null;
        }

        if (promotedShort && equality.Method is not null)
        {
            throw RowLockQueryErrors.Rejected("short-to-int member promotion requires the built-in equality operator");
        }

        if (member.Expression != parameter
            || !IsSupportedValue(value, allowPromotedShortValue)
            || member.Member is not (PropertyInfo or FieldInfo))
        {
            throw RowLockQueryErrors.Rejected("equality must compare a direct root scalar member with a constant or query parameter");
        }

        var property = entityType.FindProperty(member.Member.Name);
        if (property is null
            || property.PropertyInfo != member.Member && property.FieldInfo != member.Member
            || !ValidEqualityOperator(equality, member.Type))
        {
            throw RowLockQueryErrors.Rejected("the equality member must resolve to one exact mapped scalar property and operator");
        }
    }

    private static bool TryReadPredicateMember(
        Expression expression,
        ParameterExpression parameter,
        out MemberExpression member,
        out bool promotedShort)
    {
        if (expression is MemberExpression { Expression: ParameterExpression root } directMember
            && root == parameter)
        {
            member = directMember;
            promotedShort = false;
            return true;
        }

        if (expression is UnaryExpression
            {
                NodeType: ExpressionType.Convert,
                Method: null,
                Type: var targetType,
                IsLifted: false,
                Operand: MemberExpression { Type: var sourceType, Expression: ParameterExpression convertedRoot } convertedMember
            }
            && convertedRoot == parameter
            && sourceType == typeof(short)
            && targetType == typeof(int))
        {
            member = convertedMember;
            promotedShort = true;
            return true;
        }

        member = null!;
        promotedShort = false;
        return false;
    }

    private static bool IsSupportedValue(Expression expression, bool allowPromotedShortValue = false)
    {
        if (expression is ConstantExpression or QueryParameterExpression)
        {
            return true;
        }

        return allowPromotedShortValue
            && expression is UnaryExpression
            {
                NodeType: ExpressionType.Convert,
                Method: null,
                Type: var targetType,
                IsLifted: false,
                Operand: { Type: var sourceType } convertedValue
            }
            && sourceType == typeof(short)
            && targetType == typeof(int)
            && convertedValue is ConstantExpression or QueryParameterExpression;
    }

    private static bool ValidEqualityOperator(BinaryExpression equality, Type memberType)
    {
        if (equality.Method is null)
        {
            return true;
        }

        var method = equality.Method;
        return method.Name == "op_Equality"
            && method.IsStatic
            && method.ReturnType == typeof(bool)
            && method.GetParameters().Length == 2
            && method.DeclaringType == memberType;
    }

    private void ValidateOrdering(LambdaExpression ordering, IEntityType entityType)
    {
        _ = ReadOrderingProperty(ordering, entityType);
    }

    private static HashSet<MethodInfo> CreateQueryMethods()
    {
        var methods = new HashSet<MethodInfo>();
        AddQueryable(nameof(Queryable.Where), 2, lambdaIndex: 1, lambdaArity: 1);
        AddQueryable(nameof(Queryable.OrderBy), 2, 1, 1, 2);
        AddQueryable(nameof(Queryable.OrderByDescending), 2, 1, 1, 2);
        AddQueryable(nameof(Queryable.ThenBy), 2, 1, 1, 2);
        AddQueryable(nameof(Queryable.ThenByDescending), 2, 1, 1, 2);
        AddQueryable(nameof(Queryable.Take), 2, secondParameterType: typeof(int));
        foreach (var name in new[] { nameof(Queryable.First), nameof(Queryable.FirstOrDefault), nameof(Queryable.Single), nameof(Queryable.SingleOrDefault) })
        {
            AddQueryable(name, 1);
            AddQueryable(name, 2, 1, 1);
        }

        AddEf(nameof(EntityFrameworkQueryableExtensions.AsNoTracking), 1);
        AddEf(nameof(EntityFrameworkQueryableExtensions.AsTracking), 1);
        AddEf(nameof(EntityFrameworkQueryableExtensions.TagWith), 2);
        foreach (var name in new[]
        {
            nameof(EntityFrameworkQueryableExtensions.FirstAsync),
            nameof(EntityFrameworkQueryableExtensions.FirstOrDefaultAsync),
            nameof(EntityFrameworkQueryableExtensions.SingleAsync),
            nameof(EntityFrameworkQueryableExtensions.SingleOrDefaultAsync)
        })
        {
            AddEf(name, 2);
            AddEf(name, 3, 1, 1);
        }

        return methods;

        void AddQueryable(
            string name,
            int parameterCount,
            int lambdaIndex = -1,
            int lambdaArity = 0,
            int genericArity = 1,
            Type? secondParameterType = null)
            => Add(typeof(Queryable), name, parameterCount, lambdaIndex, lambdaArity, genericArity, secondParameterType);
        void AddEf(string name, int parameterCount, int lambdaIndex = -1, int lambdaArity = 0)
            => Add(typeof(EntityFrameworkQueryableExtensions), name, parameterCount, lambdaIndex, lambdaArity, 1, null);
        void Add(
            Type owner,
            string name,
            int parameterCount,
            int lambdaIndex,
            int lambdaArity,
            int genericArity,
            Type? secondParameterType)
        {
            var candidates = owner.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == name && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == genericArity)
                .Where(method => method.GetParameters().Length == parameterCount)
                .Where(method => lambdaIndex < 0 || IsLambdaParameter(method.GetParameters()[lambdaIndex].ParameterType, lambdaArity))
                .Where(method => secondParameterType is null || method.GetParameters()[1].ParameterType == secondParameterType)
                .ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidOperationException($"Expected one exact EF query method definition for {owner.FullName}.{name}.");
            }

            methods.Add(candidates[0]);
        }
        static bool IsLambdaParameter(Type type, int lambdaArity)
            => type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(Expression<>)
                && type.GetGenericArguments()[0].IsGenericType
                && type.GetGenericArguments()[0].GetGenericTypeDefinition().FullName?.StartsWith("System.Func`", StringComparison.Ordinal) == true
                && type.GetGenericArguments()[0].GetGenericArguments().Length == lambdaArity + 1;
    }
}
