using Microsoft.EntityFrameworkCore;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;

internal sealed class UnsupportedMappingContext(DbContextOptions<UnsupportedMappingContext> options) : DbContext(options)
{
    public DbSet<InheritanceRoot> InheritedRows => Set<InheritanceRoot>();
    public DbSet<ViewProbeRow> ViewRows => Set<ViewProbeRow>();
    public DbSet<SharedProbeRow> SharedRows => Set<SharedProbeRow>();
    public DbSet<ComplexProbeRow> ComplexRows => Set<ComplexProbeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InheritanceRoot>(entity =>
        {
            entity.ToTable("InheritanceRows");
            entity.HasKey(row => row.Id);
        });
        modelBuilder.Entity<InheritanceLeaf>();

        modelBuilder.Entity<ViewProbeRow>(entity =>
        {
            entity.ToView("Rows");
            entity.HasKey(row => row.Id);
        });

        modelBuilder.Entity<SharedProbeRow>(entity =>
        {
            entity.ToTable("SharedRows");
            entity.HasKey(row => row.Id);
            entity.HasOne(row => row.Details)
                .WithOne()
                .HasForeignKey<SharedProbeDetails>(details => details.Id);
        });
        modelBuilder.Entity<SharedProbeDetails>(entity =>
        {
            entity.ToTable("SharedRows");
            entity.HasKey(row => row.Id);
        });

        modelBuilder.Entity<ComplexProbeRow>(entity =>
        {
            entity.ToTable("ComplexRows");
            entity.HasKey(row => row.Id);
            entity.ComplexProperty(row => row.Details);
        });
    }
}

internal class InheritanceRoot
{
    public int Id { get; set; }
}

internal sealed class InheritanceLeaf : InheritanceRoot
{
    public string Payload { get; set; } = string.Empty;
}

internal sealed class ViewProbeRow
{
    public int Id { get; set; }
    public string Payload { get; set; } = string.Empty;
}

internal sealed class SharedProbeRow
{
    public int Id { get; set; }
    public SharedProbeDetails Details { get; set; } = null!;
}

internal sealed class SharedProbeDetails
{
    public int Id { get; set; }
    public string Value { get; set; } = string.Empty;
}

internal sealed class ComplexProbeRow
{
    public int Id { get; set; }
    public ComplexProbeDetails Details { get; set; } = new();
}

internal sealed class ComplexProbeDetails
{
    public string Value { get; set; } = string.Empty;
}

internal class NavigationProbeContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<NavigationRoot> Rows => Set<NavigationRoot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NavigationRoot>(entity =>
        {
            entity.ToTable("Rows");
            entity.HasKey(row => row.Id);
            entity.HasOne(row => row.Peer).WithMany().HasForeignKey(row => row.PeerId);
        });
        modelBuilder.Entity<NavigationPeer>(entity =>
        {
            entity.ToTable("Peers");
            entity.HasKey(peer => peer.Id);
        });
    }
}

internal sealed class AutoIncludeNavigationContext(DbContextOptions<AutoIncludeNavigationContext> options)
    : NavigationProbeContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<NavigationRoot>().Navigation(row => row.Peer).AutoInclude();
    }
}

internal sealed class NavigationRoot
{
    public int Id { get; set; }
    public int PeerId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public NavigationPeer Peer { get; set; } = null!;
}

internal sealed class NavigationPeer
{
    public int Id { get; set; }
    public string Payload { get; set; } = string.Empty;
}
