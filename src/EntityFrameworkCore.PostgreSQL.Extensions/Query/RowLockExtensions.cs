using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

/// <summary>Experimental query-carrier implementations compared by the PostgreSQL extensions PoC.</summary>
public enum PocCarrier
{
    /// <summary>Translates the lock marker and stores intent on the exact root table occurrence.</summary>
    TableAnnotations,

    /// <summary>Translates the lock marker and carries intent through a reserved query tag.</summary>
    TranslatorTag,

    /// <summary>Rewrites the lock marker to a reserved query tag before query translation.</summary>
    ExpressionTag
}

/// <summary>PostgreSQL row-lock strengths exposed by the experimental query PoC.</summary>
public enum LockStrength
{
    /// <summary>Acquire a FOR UPDATE lock.</summary>
    Update,

    /// <summary>Acquire a FOR NO KEY UPDATE lock.</summary>
    NoKeyUpdate,

    /// <summary>Acquire a FOR SHARE lock.</summary>
    Share,

    /// <summary>Acquire a FOR KEY SHARE lock.</summary>
    KeyShare
}

/// <summary>Behavior when a requested row lock conflicts with another transaction.</summary>
public enum LockWait
{
    /// <summary>Wait for the conflicting lock to be released.</summary>
    Wait,

    /// <summary>Raise PostgreSQL lock-not-available when a conflict exists.</summary>
    NoWait,

    /// <summary>Skip rows that cannot be locked immediately.</summary>
    SkipLocked
}

/// <summary>Experimental PostgreSQL row-locking query extensions. This API is part of a carrier PoC.</summary>
public static class RowLockExtensions
{
    private static readonly MethodInfo ForUpdateMethod = GetMethod(nameof(ForUpdate), 0);
    private static readonly MethodInfo ForUpdateWaitMethod = GetMethod(nameof(ForUpdate), 1);
    private static readonly MethodInfo ForShareMethod = GetMethod(nameof(ForShare), 0);
    private static readonly MethodInfo ForShareWaitMethod = GetMethod(nameof(ForShare), 1);
    private static readonly MethodInfo WithRowLockMethod = GetMethod(nameof(WithRowLock), 2);

    /// <summary>Requests a FOR UPDATE lock for the unambiguous root entity occurrence.</summary>
    public static IQueryable<T> ForUpdate<T>(this IQueryable<T> source)
        => Mark(source, ForUpdateMethod, Array.Empty<Expression>());

    /// <summary>Requests a FOR UPDATE lock with the selected PostgreSQL conflict behavior.</summary>
    public static IQueryable<T> ForUpdate<T>(this IQueryable<T> source, [NotParameterized] LockWait wait)
        => Mark(source, ForUpdateWaitMethod, new Expression[] { Expression.Constant(wait) });

    /// <summary>Requests a FOR SHARE lock for the unambiguous root entity occurrence.</summary>
    public static IQueryable<T> ForShare<T>(this IQueryable<T> source)
        => Mark(source, ForShareMethod, Array.Empty<Expression>());

    /// <summary>Requests a FOR SHARE lock with the selected PostgreSQL conflict behavior.</summary>
    public static IQueryable<T> ForShare<T>(this IQueryable<T> source, [NotParameterized] LockWait wait)
        => Mark(source, ForShareWaitMethod, new Expression[] { Expression.Constant(wait) });

    /// <summary>Requests a row lock with an explicit PostgreSQL lock strength and wait behavior.</summary>
    public static IQueryable<T> WithRowLock<T>(
        this IQueryable<T> source,
        [NotParameterized] LockStrength strength,
        [NotParameterized] LockWait wait)
        => Mark(source, WithRowLockMethod, new Expression[] { Expression.Constant(strength), Expression.Constant(wait) });

    internal static bool IsMarker(MethodInfo method)
    {
        var definition = method.IsGenericMethod ? method.GetGenericMethodDefinition() : method;
        return definition == ForUpdateMethod
            || definition == ForUpdateWaitMethod
            || definition == ForShareMethod
            || definition == ForShareWaitMethod
            || definition == WithRowLockMethod;
    }

    internal static bool TryRead(MethodCallExpression call, out LockRequest request)
    {
        request = default;
        if (!IsMarker(call.Method))
        {
            return false;
        }

        LockStrength strength;
        LockWait wait;
        if (call.Method.GetGenericMethodDefinition() == ForUpdateMethod)
        {
            strength = LockStrength.Update;
            wait = LockWait.Wait;
        }
        else if (call.Method.GetGenericMethodDefinition() == ForShareMethod)
        {
            strength = LockStrength.Share;
            wait = LockWait.Wait;
        }
        else if (call.Method.GetGenericMethodDefinition() == ForUpdateWaitMethod)
        {
            strength = LockStrength.Update;
            wait = ReadConstant<LockWait>(call.Arguments[1]);
        }
        else if (call.Method.GetGenericMethodDefinition() == ForShareWaitMethod)
        {
            strength = LockStrength.Share;
            wait = ReadConstant<LockWait>(call.Arguments[1]);
        }
        else
        {
            strength = ReadConstant<LockStrength>(call.Arguments[1]);
            wait = ReadConstant<LockWait>(call.Arguments[2]);
        }

        if (!Enum.IsDefined(strength) || !Enum.IsDefined(wait))
        {
            throw new InvalidOperationException(
                "The PostgreSQL row-lock request cannot be fulfilled: the row-lock mode is not a defined value.");
        }

        request = new LockRequest(strength, wait);
        return true;
    }

    private static T ReadConstant<T>(Expression expression) where T : struct, Enum
        => expression is ConstantExpression { Value: T value }
            ? value
            : throw new InvalidOperationException(
                "The PostgreSQL row-lock request cannot be fulfilled: lock strength and wait options must be compile-time constants.");

    private static IQueryable<T> Mark<T>(IQueryable<T> source, MethodInfo definition, IReadOnlyCollection<Expression> options)
    {
        ArgumentNullException.ThrowIfNull(source);
        var method = definition.MakeGenericMethod(typeof(T));
        return source.Provider.CreateQuery<T>(Expression.Call(null, method, new[] { source.Expression }.Concat(options)));
    }

    private static MethodInfo GetMethod(string name, int parameterCount)
        => typeof(RowLockExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == name && method.GetParameters().Length == parameterCount + 1);
}

internal readonly record struct LockRequest(LockStrength Strength, LockWait Wait)
{
    public string Tag(PocCarrier carrier) => $"{LockProtocol.TagPrefix}{(int)carrier}:{(int)Strength}:{(int)Wait}";
    public string Receipt(PocCarrier carrier) => $"{LockProtocol.ReceiptPrefix}{(int)carrier}:{(int)Strength}:{(int)Wait}";
}

internal static class LockProtocol
{
    public const string TagPrefix = "__pgext_lock_v1:";
    public const string ReceiptPrefix = "__pgext_lock_fulfilled_v1:";
    public const string AnnotationName = "EntityFrameworkCore.PostgreSQL.Extensions.RowLock.V1";

    public static bool ContainsReservedProtocolText(string value)
        => value.Contains(TagPrefix, StringComparison.Ordinal)
            || value.Contains(ReceiptPrefix, StringComparison.Ordinal);
}

/// <summary>Registers one experimental row-lock carrier for the selected context options.</summary>
public static class PostgreSqlExtensionsOptionsBuilderExtensions
{
    /// <summary>Enables an experimental row-lock query carrier for this Npgsql context.</summary>
    public static NpgsqlDbContextOptionsBuilder UsePostgreSqlExtensions(
        this NpgsqlDbContextOptionsBuilder optionsBuilder,
        PocCarrier carrier)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        if (!Enum.IsDefined(carrier))
        {
            throw new ArgumentOutOfRangeException(nameof(carrier));
        }

        var builder = ((IRelationalDbContextOptionsBuilderInfrastructure)optionsBuilder).OptionsBuilder;
        var existing = builder.Options.FindExtension<RowLockOptionsExtension>();
        if (existing is not null && existing.Carrier != carrier)
        {
            throw new InvalidOperationException("Conflicting PostgreSQL extension query carriers were configured for one context.");
        }

        if (existing is not null)
        {
            return optionsBuilder;
        }

        var priorQueryServiceReplacement = builder.Options.FindExtension<CoreOptionsExtension>()?
            .ReplacedServices?.Keys
            .Select(replacement => replacement.Item1)
            .FirstOrDefault(serviceType => serviceType == typeof(IQuerySqlGeneratorFactory)
                || serviceType == typeof(IQueryableMethodTranslatingExpressionVisitorFactory));
        if (priorQueryServiceReplacement is not null)
        {
            throw new InvalidOperationException(
                $"Cannot configure the PostgreSQL row-lock carrier because '{priorQueryServiceReplacement.FullName}' already has a service replacement.");
        }

        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new RowLockOptionsExtension(carrier));
        builder.ReplaceService<IQuerySqlGeneratorFactory, RowLockQuerySqlGeneratorFactory>();
        if (carrier == PocCarrier.TableAnnotations)
        {
            builder.ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, AnnotationLockQueryVisitorFactory>();
        }
        else if (carrier == PocCarrier.TranslatorTag)
        {
            builder.ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, TagLockQueryVisitorFactory>();
        }

        builder.AddInterceptors(RowLockQueryInterceptor.For(carrier));
        return optionsBuilder;
    }
}

internal sealed class RowLockOptionsExtension : IDbContextOptionsExtension
{
    public RowLockOptionsExtension(PocCarrier carrier)
    {
        Carrier = carrier;
        Info = new ExtensionInfo(this);
    }

    public PocCarrier Carrier { get; }
    public DbContextOptionsExtensionInfo Info { get; }

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        private readonly RowLockOptionsExtension _extension;

        public ExtensionInfo(RowLockOptionsExtension extension) : base(extension) => _extension = extension;

        public override bool IsDatabaseProvider => false;
        public override string LogFragment => $"RowLockCarrier={_extension.Carrier} ";
        public override int GetServiceProviderHashCode() => (int)_extension.Carrier;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo info && info._extension.Carrier == _extension.Carrier;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["EntityFrameworkCore.PostgreSQL.Extensions:RowLockCarrier"] = _extension.Carrier.ToString();
    }
}
