using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace EntityFrameworkCore.PostgreSQL.Extensions;

/// <summary>PostgreSQL row-lock strengths supported by the row-lock query extensions.</summary>
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

/// <summary>PostgreSQL row-locking query extensions for one directly mapped root entity table.</summary>
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
}

internal sealed record LockQueryIntent(
    LockStrength Strength,
    LockWait Wait,
    string EntityTypeName,
    string TableName,
    string? Schema,
    string? TakeParameterName,
    bool HasTake)
{
    public string Tag => LockProtocol.Format(LockProtocol.TagPrefix, this);
    public string Receipt => LockProtocol.Format(LockProtocol.ReceiptPrefix, this);
}

internal static class LockProtocol
{
    public const string TagPrefix = "__pgext_lock_v1:";
    public const string ReceiptPrefix = "__pgext_lock_fulfilled_v1:";

    public static bool ContainsReservedProtocolText(string value)
        => value.Contains(TagPrefix, StringComparison.Ordinal)
            || value.Contains(ReceiptPrefix, StringComparison.Ordinal);

    public static string Format(string prefix, LockQueryIntent request)
    {
        var take = request.TakeParameterName is null
            ? "-"
            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.TakeParameterName))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var table = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.TableName))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var entity = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.EntityTypeName))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var schema = request.Schema is null
            ? "-"
            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.Schema))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{prefix}{(int)request.Strength}:{(int)request.Wait}:{(request.HasTake ? 1 : 0)}:{take}:{entity}:{table}:{schema}";
    }

    public static bool TryParseIntent(string text, string prefix, out LockQueryIntent intent)
    {
        intent = null!;
        var parts = text.Split(':');
        if (parts.Length != 7
            || !int.TryParse(parts[0], out var strengthValue)
            || !Enum.IsDefined((LockStrength)strengthValue)
            || !int.TryParse(parts[1], out var waitValue)
            || !Enum.IsDefined((LockWait)waitValue)
            || parts[2] is not ("0" or "1")
            || parts[2] == "0" && parts[3] != "-"
            || !TryDecode(parts[4], out var entityTypeName)
            || entityTypeName.Length == 0
            || !TryDecode(parts[5], out var tableName)
            || tableName.Length == 0)
        {
            return false;
        }

        string? takeParameterName = null;
        if (parts[3] != "-")
        {
            if (!TryDecode(parts[3], out takeParameterName) || string.IsNullOrEmpty(takeParameterName))
            {
                return false;
            }
        }

        string? schema = null;
        if (parts[6] != "-" && !TryDecode(parts[6], out schema))
        {
            return false;
        }

        intent = new LockQueryIntent(
            (LockStrength)strengthValue,
            (LockWait)waitValue,
            entityTypeName,
            tableName,
            schema,
            takeParameterName,
            parts[2] == "1");
        return Format(prefix, intent) == prefix + text;
    }

    private static bool TryDecode(string value, out string decoded)
    {
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            decoded = string.Empty;
            return false;
        }
    }
}

/// <summary>Registers PostgreSQL row-lock query support for the selected context options.</summary>
public static class PostgreSqlExtensionsOptionsBuilderExtensions
{
    /// <summary>Enables PostgreSQL row-lock query support for this Npgsql context.</summary>
    public static NpgsqlDbContextOptionsBuilder UsePostgreSqlExtensions(this NpgsqlDbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var builder = ((IRelationalDbContextOptionsBuilderInfrastructure)optionsBuilder).OptionsBuilder;
        var existing = builder.Options.FindExtension<RowLockOptionsExtension>();
        var replacements = builder.Options.FindExtension<CoreOptionsExtension>()?.ReplacedServices;
        var conflictingReplacement = replacements?.FirstOrDefault(replacement =>
            replacement.Key.Item1 == typeof(IQueryableMethodTranslatingExpressionVisitorFactory)
            || replacement.Key.Item1 == typeof(IQuerySqlGeneratorFactory)
                && (existing is null || replacement.Value != typeof(RowLockQuerySqlGeneratorFactory)));
        var conflictingService = conflictingReplacement?.Key.Item1;
        if (conflictingService is not null)
        {
            throw new InvalidOperationException(
                $"Cannot configure PostgreSQL row-lock support because '{conflictingService.FullName}' already has a competing service replacement.");
        }

        if (builder.Options.FindExtension<CoreOptionsExtension>()?.InternalServiceProvider is not null)
        {
            throw new InvalidOperationException(
                "PostgreSQL row-lock support does not support manually configured EF internal service providers.");
        }

        if (existing is not null)
        {
            return optionsBuilder;
        }

        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new RowLockOptionsExtension());
        builder.ReplaceService<IQuerySqlGeneratorFactory, RowLockQuerySqlGeneratorFactory>();
        builder.AddInterceptors(RowLockQueryInterceptor.Instance);
        return optionsBuilder;
    }
}

internal sealed class RowLockOptionsExtension : IDbContextOptionsExtension
{
    public RowLockOptionsExtension()
    {
        Info = new ExtensionInfo(this);
    }

    public DbContextOptionsExtensionInfo Info { get; }

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
        if (options.FindExtension<CoreOptionsExtension>()?.InternalServiceProvider is not null)
        {
            throw new InvalidOperationException(
                "PostgreSQL row-lock support does not support manually configured EF internal service providers.");
        }
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(RowLockOptionsExtension extension) : base(extension)
        {
        }

        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "PostgreSqlExtensionsRowLock ";
        public override int GetServiceProviderHashCode() => 1;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["EntityFrameworkCore.PostgreSQL.Extensions:RowLock"] = "C";
    }
}
