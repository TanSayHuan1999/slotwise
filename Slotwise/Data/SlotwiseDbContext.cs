using Microsoft.EntityFrameworkCore;
using Slotwise.Domain;

namespace Slotwise.Data;

public class SlotwiseDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public SlotwiseDbContext(DbContextOptions<SlotwiseDbContext> options, ITenantContext tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    /// <summary>
    /// The tenant the current unit of work is scoped to. Query filters
    /// reference this property so EF re-evaluates it on every query.
    /// </summary>
    public Guid? CurrentTenantId => _tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();
    public DbSet<WorkingHours> WorkingHours => Set<WorkingHours>();
    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // --- Multi-tenancy -------------------------------------------------
        // Every tenant-owned entity is filtered to the current tenant. With
        // no tenant selected, TenantId comparisons against NULL match
        // nothing (fail closed). The Tenant entity itself is visible to all
        // only while no tenant is selected, which is what slug resolution
        // and tenant creation need; once scoped, a tenant sees only itself.
        modelBuilder.Entity<Tenant>()
            .HasQueryFilter(t => CurrentTenantId == null || t.Id == CurrentTenantId);
        modelBuilder.Entity<Service>()
            .HasQueryFilter(s => s.TenantId == CurrentTenantId);
        modelBuilder.Entity<StaffMember>()
            .HasQueryFilter(s => s.TenantId == CurrentTenantId);
        modelBuilder.Entity<WorkingHours>()
            .HasQueryFilter(w => w.TenantId == CurrentTenantId);
        modelBuilder.Entity<Booking>()
            .HasQueryFilter(b => b.TenantId == CurrentTenantId);

        // --- Shape ---------------------------------------------------------
        modelBuilder.Entity<Tenant>(e =>
        {
            e.HasIndex(t => t.Slug).IsUnique();
            e.Property(t => t.Name).HasMaxLength(200);
            e.Property(t => t.Slug).HasMaxLength(100);
        });

        modelBuilder.Entity<Service>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(200);
            e.HasIndex(s => s.TenantId);
        });

        modelBuilder.Entity<StaffMember>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(200);
            e.HasIndex(s => s.TenantId);
        });

        modelBuilder.Entity<WorkingHours>(e =>
        {
            e.HasIndex(w => new { w.StaffMemberId, w.DayOfWeek });
        });

        modelBuilder.Entity<Booking>(e =>
        {
            e.Property(b => b.CustomerName).HasMaxLength(200);
            e.Property(b => b.CustomerContact).HasMaxLength(200);
            // Status is stored as its int value; the no-overlap exclusion
            // constraint's WHERE clause keys off Status = 0 (Confirmed).
            e.Property(b => b.Status).HasConversion<int>();
            e.HasIndex(b => new { b.StaffMemberId, b.StartUtc });
            e.HasIndex(b => b.TenantId);
        });
    }
}
