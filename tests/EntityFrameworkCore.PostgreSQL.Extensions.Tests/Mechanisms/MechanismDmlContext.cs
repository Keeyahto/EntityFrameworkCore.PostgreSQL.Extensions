using Microsoft.EntityFrameworkCore;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Mechanisms;

internal sealed class MechanismDmlContext(DbContextOptions<MechanismDmlContext> options) : DbContext(options)
{
    public DbSet<IdentityDefaultRow> IdentityDefaults => Set<IdentityDefaultRow>();
    public DbSet<CompositeUniqueRow> CompositeUnique => Set<CompositeUniqueRow>();
    public DbSet<ConvertedUniqueRow> ConvertedUnique => Set<ConvertedUniqueRow>();
    public DbSet<NullableUniqueRow> NullableUnique => Set<NullableUniqueRow>();
    public DbSet<SecondaryUniqueRow> SecondaryUnique => Set<SecondaryUniqueRow>();
    public DbSet<PartialUniqueRow> PartialUnique => Set<PartialUniqueRow>();
    public DbSet<ShadowValueRow> ShadowValues => Set<ShadowValueRow>();
    public DbSet<SplitRow> SplitRows => Set<SplitRow>();
    public DbSet<NullableDefaultRow> NullableDefaults => Set<NullableDefaultRow>();
    public DbSet<IdentityAlwaysRow> IdentityAlways => Set<IdentityAlwaysRow>();
    public DbSet<ClientGeneratedRow> ClientGenerated => Set<ClientGeneratedRow>();
    public DbSet<ThrowingConvertedRow> ThrowingConverted => Set<ThrowingConvertedRow>();
    public DbSet<OwnedHostRow> OwnedHosts => Set<OwnedHostRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdentityDefaultRow>(entity =>
        {
            entity.ToTable("IdentityDefaults", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityByDefaultColumn();
            entity.Property(row => row.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(row => row.UniqueKey).IsUnique();
        });

        modelBuilder.Entity<CompositeUniqueRow>(entity =>
        {
            entity.ToTable("CompositeUnique", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityByDefaultColumn();
            entity.HasIndex(row => new { row.Left, row.Right }).IsUnique();
        });

        modelBuilder.Entity<ConvertedUniqueRow>(entity =>
        {
            entity.ToTable("ConvertedUnique", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityByDefaultColumn();
            entity.Property(row => row.Key)
                .HasConversion(new ValueConverter<ConvertedKey, string>(key => key.Value, value => new ConvertedKey(value)))
                .HasColumnName("KeyValue");
            entity.HasIndex(row => row.Key).IsUnique();
        });

        modelBuilder.Entity<NullableUniqueRow>(entity =>
        {
            entity.ToTable("NullableUnique", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityByDefaultColumn();
            entity.HasIndex(row => row.Key).IsUnique();
        });

        modelBuilder.Entity<SecondaryUniqueRow>(entity =>
        {
            entity.ToTable("SecondaryUnique", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityByDefaultColumn();
            entity.HasIndex(row => row.TargetKey).IsUnique();
            entity.HasIndex(row => row.SecondaryKey).IsUnique();
        });

        modelBuilder.Entity<PartialUniqueRow>(entity =>
        {
            entity.ToTable("PartialUnique", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityByDefaultColumn();
            entity.HasIndex(row => row.Key).IsUnique().HasFilter("\"Enabled\" = TRUE");
        });

        modelBuilder.Entity<ShadowValueRow>(entity =>
        {
            entity.ToTable("ShadowValues", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityByDefaultColumn();
            entity.Property<string>("ShadowValue").IsRequired();
            entity.HasIndex(row => row.Key).IsUnique();
        });

        modelBuilder.Entity<SplitRow>(entity =>
        {
            entity.ToTable("SplitRows", "mechanisms");
            entity.SplitToTable("SplitRowDetails", "mechanisms", table =>
                table.Property(row => row.Detail).HasColumnName("Detail"));
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityByDefaultColumn();
            entity.HasIndex(row => row.Key).IsUnique();
        });

        modelBuilder.Entity<NullableDefaultRow>(entity =>
        {
            entity.ToTable("NullableDefaults", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityByDefaultColumn();
            entity.Property(row => row.Value).HasDefaultValueSql("'database-default'").HasSentinel("use-database-default");
            entity.HasIndex(row => row.UniqueKey).IsUnique();
        });

        modelBuilder.Entity<IdentityAlwaysRow>(entity =>
        {
            entity.ToTable("IdentityAlways", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityAlwaysColumn();
            entity.HasIndex(row => row.UniqueKey).IsUnique();
        });

        modelBuilder.Entity<ClientGeneratedRow>(entity =>
        {
            entity.ToTable("ClientGenerated", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedOnAdd();
            entity.HasIndex(row => row.UniqueKey).IsUnique();
        });

        modelBuilder.Entity<ThrowingConvertedRow>(entity =>
        {
            entity.ToTable("ThrowingConverted", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityByDefaultColumn();
            entity.Property(row => row.Key).HasConversion(
                key => ThrowingKeyConverter.ToProvider(key),
                value => new ThrowingKey(value));
            entity.HasIndex(row => row.Key).IsUnique();
        });

        modelBuilder.Entity<OwnedHostRow>(entity =>
        {
            entity.ToTable("OwnedHosts", "mechanisms");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).UseIdentityByDefaultColumn();
            entity.OwnsOne(row => row.Details, owned => owned.ToTable("OwnedHosts", "mechanisms"));
        });
    }
}

internal static class MechanismDmlDatabase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    public static MechanismDmlContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<MechanismDmlContext>().UseProviderTestWarnings().UseNpgsql(connectionString).Options);

    public static async Task ResetAsync(string connectionString)
    {
        await EnsureSchemaScriptAppliedAsync(connectionString);
        await using var context = CreateContext(connectionString);
        await context.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE mechanisms.\"IdentityDefaults\", mechanisms.\"CompositeUnique\", mechanisms.\"ConvertedUnique\", mechanisms.\"NullableUnique\", mechanisms.\"SecondaryUnique\", mechanisms.\"PartialUnique\", mechanisms.\"ShadowValues\", mechanisms.\"SplitRows\", mechanisms.\"SplitRowDetails\", mechanisms.\"NullableDefaults\", mechanisms.\"IdentityAlways\", mechanisms.\"ClientGenerated\", mechanisms.\"ThrowingConverted\", mechanisms.\"OwnedHosts\" RESTART IDENTITY");
    }

    public static async Task EnsureSchemaScriptAppliedAsync(string connectionString)
    {
        if (_created)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            if (_created)
            {
                return;
            }

            await using var context = CreateContext(connectionString);
            var script = context.Database.GenerateCreateScript();
            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync();
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = script;
                await command.ExecuteNonQueryAsync();
                _created = true;
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}

internal sealed record ConvertedKey(string Value);

internal sealed class IdentityDefaultRow
{
    public int Id { get; set; }
    public string UniqueKey { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

internal sealed class CompositeUniqueRow
{
    public int Id { get; set; }
    public string Left { get; set; } = string.Empty;
    public string Right { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
}

internal sealed class ConvertedUniqueRow
{
    public int Id { get; set; }
    public ConvertedKey Key { get; set; } = new(string.Empty);
    public string Payload { get; set; } = string.Empty;
}

internal sealed class NullableUniqueRow
{
    public int Id { get; set; }
    public string? Key { get; set; }
}

internal sealed class SecondaryUniqueRow
{
    public int Id { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public string SecondaryKey { get; set; } = string.Empty;
}

internal sealed class PartialUniqueRow
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

internal sealed class ShadowValueRow
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
}

internal sealed class SplitRow
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

internal sealed class NullableDefaultRow
{
    public int Id { get; set; }
    public string UniqueKey { get; set; } = string.Empty;
    public string? Value { get; set; } = "use-database-default";
}

internal sealed class IdentityAlwaysRow
{
    public int Id { get; set; }
    public string UniqueKey { get; set; } = string.Empty;
}

internal sealed class ClientGeneratedRow
{
    public Guid Id { get; set; }
    public string UniqueKey { get; set; } = string.Empty;
}

internal sealed class ThrowingConvertedRow
{
    public int Id { get; set; }
    public ThrowingKey Key { get; set; } = new("conversion-fails");
}

internal sealed record ThrowingKey(string Value);

internal static class ThrowingKeyConverter
{
    public static string ToProvider(ThrowingKey key) => throw new InvalidOperationException("forced value-converter failure");
}

internal sealed class OwnedHostRow
{
    public int Id { get; set; }
    public OwnedMechanismDetails Details { get; set; } = new();
}

internal sealed class OwnedMechanismDetails
{
    public string Value { get; set; } = string.Empty;
}
