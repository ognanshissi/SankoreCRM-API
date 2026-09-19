namespace Sankore.Modules.Leads.Infrastructure;

using System.Text.Json;
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
    public DbSet<ScoreHistory> ScoreHistories => Set<ScoreHistory>();
    public DbSet<LeadActivity> LeadActivities => Set<LeadActivity>();
    public DbSet<LeadReminder> LeadReminders => Set<LeadReminder>();
    public DbSet<LeadTag> LeadTags => Set<LeadTag>();
    public DbSet<DuplicateDismissal> DuplicateDismissals => Set<DuplicateDismissal>();
    public DbSet<LeadMerge> LeadMerges => Set<LeadMerge>();
    public DbSet<LeadConsent> LeadConsents => Set<LeadConsent>();
    public DbSet<QualificationTemplate> QualificationTemplates => Set<QualificationTemplate>();
    public DbSet<QualificationResponse> QualificationResponses => Set<QualificationResponse>();
    public DbSet<QualificationSection> QualificationSections => Set<QualificationSection>();
    public DbSet<LeadOwnerAssignmentHistory> LeadOwnerAssignmentHistories => Set<LeadOwnerAssignmentHistory>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LeadsDbContext).Assembly);
        
        modelBuilder.HasDefaultSchema("leads");

        modelBuilder.Entity<LeadAssignment>(b =>
        {
            
        });

        modelBuilder.Entity<DispatchingRule>(b =>
        {
            b.ToTable("dispatching_rules");
            b.HasKey(r => r.Id);
            b.Property(r => r.Strategy).HasConversion(
                v => v.ToString(),
                v => Enum.Parse<DispatchingStrategy>(v))
                .HasMaxLength(30);
            b.OwnsOne(r => r.Weights, w =>
            {
                w.Property(x => x.Language).HasColumnName("weight_language");
                w.Property(x => x.Product).HasColumnName("weight_product");
                w.Property(x => x.Geography).HasColumnName("weight_geography");
                w.Property(x => x.Workload).HasColumnName("weight_workload");
                w.Property(x => x.Performance).HasColumnName("weight_performance");
            });
            b.Property(r => r.ExcludedAgentIds)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => (IReadOnlyList<Guid>)(JsonSerializer.Deserialize<List<Guid>>(v, (JsonSerializerOptions?)null) ?? new List<Guid>()));
            b.HasIndex(r => new { r.TenantId, r.Strategy, r.IsActive, r.Priority });
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

        modelBuilder.Entity<ScoreHistory>()
            .HasQueryFilter(s => s.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<LeadActivity>()
            .HasQueryFilter(a => a.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<LeadReminder>()
            .HasQueryFilter(r => r.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<LeadTag>()
            .HasQueryFilter(t => t.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<DuplicateDismissal>()
            .HasQueryFilter(d => d.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<LeadMerge>()
            .HasQueryFilter(m => m.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<LeadConsent>()
            .HasQueryFilter(c => c.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<QualificationTemplate>()
            .HasQueryFilter(t => t.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<QualificationResponse>()
            .HasQueryFilter(r => r.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<LeadOwnerAssignmentHistory>()
            .HasQueryFilter(h => h.TenantId == tenant.CurrentTenantId);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        optionsBuilder.EnableSensitiveDataLogging();
    }
}
