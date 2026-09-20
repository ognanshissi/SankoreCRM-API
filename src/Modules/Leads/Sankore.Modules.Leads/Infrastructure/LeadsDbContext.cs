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
    public DbSet<CrmTask> CrmTasks => Set<CrmTask>();
    public DbSet<TaskReassignment> TaskReassignments => Set<TaskReassignment>();
    public DbSet<TaskDecline> TaskDeclines => Set<TaskDecline>();
    public DbSet<TaskGenerationRule> TaskGenerationRules => Set<TaskGenerationRule>();
    public DbSet<LeadImportJob> LeadImportJobs => Set<LeadImportJob>();
    public DbSet<LeadSourceConfig> LeadSourceConfigs => Set<LeadSourceConfig>();
    public DbSet<ScoringConfig> ScoringConfigs => Set<ScoringConfig>();
    public DbSet<TaskTypeConfig> TaskTypeConfigs => Set<TaskTypeConfig>();
    public DbSet<PipelineStageConfig> PipelineStageConfigs => Set<PipelineStageConfig>();
    public DbSet<SlaConfig> SlaConfigs => Set<SlaConfig>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<NurturingSequence> NurturingSequences => Set<NurturingSequence>();
    public DbSet<NurturingStep> NurturingSteps => Set<NurturingStep>();
    public DbSet<NurturingEnrollment> NurturingEnrollments => Set<NurturingEnrollment>();
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
                w.Property(x => x.Agency).HasColumnName("weight_agency");
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

        modelBuilder.Entity<NurturingSequence>(b =>
        {
            b.ToTable("nurturing_sequences");
            b.HasKey(s => s.Id);
            b.Property(s => s.Name).HasMaxLength(200).IsRequired();
            b.Property(s => s.Description).HasMaxLength(2000);
            b.HasMany(s => s.Steps).WithOne().HasForeignKey(st => st.SequenceId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(s => s.TenantId == tenant.CurrentTenantId);
        });

        modelBuilder.Entity<NurturingStep>(b =>
        {
            b.ToTable("nurturing_steps");
            b.HasKey(st => st.Id);
            b.Property(st => st.EmailTemplateKey).HasMaxLength(100).IsRequired();
            b.Property(st => st.Subject).HasMaxLength(200);
            b.HasIndex(st => new { st.SequenceId, st.Order });
        });

        modelBuilder.Entity<NurturingEnrollment>(b =>
        {
            b.ToTable("nurturing_enrollments");
            b.HasKey(e => e.Id);
            b.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(20);
            b.Property(e => e.CancellationReason).HasMaxLength(500);
            b.HasIndex(e => new { e.TenantId, e.Status, e.NextStepDueAt });
            b.HasIndex(e => new { e.LeadId, e.Status });
            b.HasQueryFilter(e => e.TenantId == tenant.CurrentTenantId);
        });

        modelBuilder.Entity<Opportunity>(b =>
        {
            b.ToTable("opportunities");
            b.HasKey(o => o.Id);
            b.Property(o => o.Title).HasMaxLength(200).IsRequired();
            b.Property(o => o.Description).HasMaxLength(2000);
            b.Property(o => o.Product).HasMaxLength(100).IsRequired();
            b.Property(o => o.CustomerEntityType).HasMaxLength(50);
            b.Property(o => o.CloseReason).HasMaxLength(500);
            b.Property(o => o.Stage)
                .HasConversion<string>()
                .HasMaxLength(30);
            b.OwnsOne(o => o.EstimatedAmount, m =>
            {
                m.Property(p => p.Amount).HasColumnName("estimated_amount").HasPrecision(18, 4);
                m.Property(p => p.Currency).HasColumnName("estimated_currency").HasMaxLength(3);
            });
            b.HasIndex(o => new { o.TenantId, o.LeadId });
            b.HasIndex(o => new { o.TenantId, o.CustomerEntityId })
                .HasFilter("customer_entity_id IS NOT NULL");
            b.HasIndex(o => new { o.TenantId, o.Stage });
            b.Ignore(o => o.DomainEvents);
            b.HasQueryFilter(o => o.TenantId == tenant.CurrentTenantId);
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

        modelBuilder.Entity<CrmTask>()
            .HasQueryFilter(t => t.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<TaskReassignment>()
            .HasQueryFilter(r => r.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<TaskDecline>()
            .HasQueryFilter(d => d.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<TaskGenerationRule>()
            .HasQueryFilter(r => r.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<LeadImportJob>(b =>
        {
            b.ToTable("lead_import_jobs");
            b.HasKey(j => j.Id);
            b.Property(j => j.Status).HasConversion(
                v => v.ToString(),
                v => Enum.Parse<LeadImportStatus>(v))
                .HasMaxLength(20);
            b.HasQueryFilter(j => j.TenantId == tenant.CurrentTenantId);
        });

        // ── Configuration entities (US-M13-190..197) ────────────────────

        modelBuilder.Entity<LeadSourceConfig>(b =>
        {
            b.ToTable("lead_source_configs");
            b.HasKey(e => e.Id);
            b.Property(e => e.Code).HasMaxLength(30);
            b.Property(e => e.Label).HasMaxLength(100);
            b.Property(e => e.Description).HasMaxLength(500);
            b.HasIndex(e => new { e.TenantId, e.Code }).IsUnique();
            b.HasQueryFilter(e => e.TenantId == tenant.CurrentTenantId);
        });

        modelBuilder.Entity<ScoringConfig>(b =>
        {
            b.ToTable("scoring_configs");
            b.HasKey(e => e.Id);
            b.Property(e => e.Name).HasMaxLength(100);
            b.HasIndex(e => new { e.TenantId, e.IsActive });
            b.HasQueryFilter(e => e.TenantId == tenant.CurrentTenantId);
        });

        modelBuilder.Entity<TaskTypeConfig>(b =>
        {
            b.ToTable("task_type_configs");
            b.HasKey(e => e.Id);
            b.Property(e => e.Code).HasMaxLength(30);
            b.Property(e => e.Label).HasMaxLength(100);
            b.Property(e => e.Description).HasMaxLength(500);
            b.HasIndex(e => new { e.TenantId, e.Code }).IsUnique();
            b.HasQueryFilter(e => e.TenantId == tenant.CurrentTenantId);
        });

        modelBuilder.Entity<PipelineStageConfig>(b =>
        {
            b.ToTable("pipeline_stage_configs");
            b.HasKey(e => e.Id);
            b.Property(e => e.Code).HasMaxLength(30);
            b.Property(e => e.Label).HasMaxLength(100);
            b.Property(e => e.Description).HasMaxLength(500);
            b.Property(e => e.Color).HasMaxLength(7);
            b.HasIndex(e => new { e.TenantId, e.Code }).IsUnique();
            b.HasQueryFilter(e => e.TenantId == tenant.CurrentTenantId);
        });

        modelBuilder.Entity<SlaConfig>(b =>
        {
            b.ToTable("sla_configs");
            b.HasKey(e => e.Id);
            b.Property(e => e.Name).HasMaxLength(100);
            b.HasIndex(e => new { e.TenantId, e.AgencyId, e.IsActive });
            b.HasQueryFilter(e => e.TenantId == tenant.CurrentTenantId);
        });
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        optionsBuilder.EnableSensitiveDataLogging();
    }
}
