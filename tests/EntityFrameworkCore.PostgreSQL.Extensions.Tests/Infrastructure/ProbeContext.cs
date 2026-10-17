using Microsoft.EntityFrameworkCore;

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

public sealed class ProbeRow
{
    public int Id { get; set; }

    public int PeerId { get; set; }

    public int Rank { get; set; }

    public string Payload { get; set; } = string.Empty;

    public List<ProbeChild> Children { get; set; } = new();
}

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
