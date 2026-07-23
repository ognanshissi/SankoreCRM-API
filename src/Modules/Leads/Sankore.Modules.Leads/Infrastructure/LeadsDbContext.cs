namespace Sankore.Modules.Leads.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;

public sealed class LeadsDbContext(DbContextOptions<LeadsDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadAssignment> LeadAssignments => Set<LeadAssignment>();
    public DbSet<DispatchingRule> DispatchingRules => Set<DispatchingRule>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("leads");

        modelBuilder.Entity<Lead>(b =>
        {
            b.ToTable("leads");
            b.HasKey(l => l.Id);
            b.Property(l => l.FullName).HasMaxLength(200).IsRequired();
            b.Property(l => l.PhoneNumber).HasMaxLength(30).IsRequired();
            b.Property(l => l.InterestedProduct).HasMaxLength(100).IsRequired();
            b.Property(l => l.PreferredLanguage).HasMaxLength(50).IsRequired();
            b.Property(l => l.Status).HasConversion<string>().HasMaxLength(30);
            b.Property(l => l.Source).HasConversion<string>().HasMaxLength(30);

            b.OwnsOne(l => l.Location, loc =>
            {
                loc.Property(p => p.Latitude).HasColumnName("lat");
                loc.Property(p => p.Longitude).HasColumnName("lng");
            });

            b.HasIndex(l => new { l.TenantId, l.Status });
            b.HasIndex(l => l.PhoneNumber);

            // Domain events are transient, never persisted as a column.
            b.Ignore(l => l.DomainEvents);
        });

        modelBuilder.Entity<LeadAssignment>(b =>
        {
            b.ToTable("lead_assignments");
            b.HasKey(a => a.Id);
            b.Property(a => a.Strategy).HasConversion<string>().HasMaxLength(30);
            b.HasIndex(a => a.LeadId);
            b.HasIndex(a => a.AgentId);
            b.HasIndex(a => new { a.SlaDeadline, a.FirstContactAt });

            b.HasOne<Lead>()
                .WithMany()
                .HasForeignKey(a => a.LeadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DispatchingRule>(b =>
        {
            b.ToTable("dispatching_rules");
            b.HasKey(r => r.Id);
            b.Property(r => r.Strategy).HasConversion<string>().HasMaxLength(30);
            b.OwnsOne(r => r.Weights, w =>
            {
                w.Property(x => x.Language).HasColumnName("weight_language");
                w.Property(x => x.Product).HasColumnName("weight_product");
                w.Property(x => x.Geography).HasColumnName("weight_geography");
                w.Property(x => x.Workload).HasColumnName("weight_workload");
                w.Property(x => x.Performance).HasColumnName("weight_performance");
            });
            b.HasIndex(r => new { r.TenantId, r.IsActive });
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(m => m.Id);
            b.HasIndex(m => new { m.ProcessedAt, m.OccurredAt });
        });

        // Multi-tenant defense in depth: applied at the ORM level so that
        // even a handler bug can never leak another IMF's leads.
        modelBuilder.Entity<Lead>()
            .HasQueryFilter(l => l.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<LeadAssignment>()
            .HasQueryFilter(a => a.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<DispatchingRule>()
            .HasQueryFilter(r => r.TenantId == tenant.CurrentTenantId);
    }
}
