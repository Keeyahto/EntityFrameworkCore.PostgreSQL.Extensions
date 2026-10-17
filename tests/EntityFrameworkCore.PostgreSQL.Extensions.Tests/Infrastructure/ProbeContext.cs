using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;

public class ProbeContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<ProbeRow> Rows => Set<ProbeRow>();

    public DbSet<PeerRow> Peers => Set<PeerRow>();

    public DbSet<ProbeChild> Children => Set<ProbeChild>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProbeRow>(entity =>
        {
            entity.ToTable("Rows");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Payload).IsRequired();
            entity.Property(row => row.NaturalCode).IsRequired();
            entity.Property(row => row.CompositeLeft).IsRequired();
            entity.Property(row => row.CompositeRight).IsRequired();
            entity.Property(row => row.ConvertedKey)
                .HasConversion(new ValueConverter<ConvertedProbeKey, string>(key => key.Value, value => new ConvertedProbeKey(value)))
                .HasColumnName("ConvertedKey");
            entity.HasAlternateKey(row => row.NaturalCode);
            entity.HasAlternateKey(row => new { row.CompositeLeft, row.CompositeRight });
            entity.HasAlternateKey(row => row.ConvertedKey);
            entity.HasMany(row => row.Children)
                .WithOne(child => child.Row)
                .HasForeignKey(child => child.RowId);
        });

        modelBuilder.Entity<PeerRow>(entity =>
        {
            entity.ToTable("Peers");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Payload).IsRequired();
        });

        modelBuilder.Entity<ProbeChild>(entity =>
        {
            entity.ToTable("Children");
            entity.HasKey(child => child.Id);
            entity.Property(child => child.Payload).IsRequired();
        });
    }
}

public sealed class ProbeContextV14(DbContextOptions<ProbeContextV14> options) : ProbeContext(options)
{
}

public sealed class FilteredProbeContext(DbContextOptions<FilteredProbeContext> options) : ProbeContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ProbeRow>().HasQueryFilter(row => row.Id > 0);
    }
}

public sealed class NamedFilteredProbeContext(DbContextOptions<NamedFilteredProbeContext> options) : ProbeContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ProbeRow>().HasQueryFilter("active-row", row => !row.IsDeleted);
    }
}

public sealed class ProbeRow
{
    public int Id { get; set; }

    public int PeerId { get; set; }

    public int Rank { get; set; }

    public string NaturalCode { get; set; } = $"natural-{Guid.NewGuid():N}";

    public string CompositeLeft { get; set; } = $"left-{Guid.NewGuid():N}";

    public string CompositeRight { get; set; } = $"right-{Guid.NewGuid():N}";

    public int? NullableKey { get; set; }

    public ConvertedProbeKey ConvertedKey { get; set; } = new($"converted-{Guid.NewGuid():N}");

    public bool IsDeleted { get; set; }

    public string Payload { get; set; } = string.Empty;

    public List<ProbeChild> Children { get; set; } = new();

}

public sealed record ConvertedProbeKey(string Value);

public sealed class PeerRow
{
    public int Id { get; set; }

    public string Payload { get; set; } = string.Empty;
}

public sealed class ProbeChild
{
    public int Id { get; set; }

    public int RowId { get; set; }

    public string Payload { get; set; } = string.Empty;

    public ProbeRow Row { get; set; } = null!;
}
