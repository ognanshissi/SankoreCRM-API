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
    public DbSet<LeadSourceRun> LeadSourceRuns => Set<LeadSourceRun>();
    public DbSet<LeadIngestion> LeadIngestions => Set<LeadIngestion>();
    public DbSet<ScoringConfig> ScoringConfigs => Set<ScoringConfig>();
    public DbSet<TaskTypeConfig> TaskTypeConfigs => Set<TaskTypeConfig>();
    public DbSet<PipelineStageConfig> PipelineStageConfigs => Set<PipelineStageConfig>();
    public DbSet<SlaConfig> SlaConfigs => Set<SlaConfig>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<NurturingSequence> NurturingSequences => Set<NurturingSequence>();
    public DbSet<NurturingStep> NurturingSteps => Set<NurturingStep>();
    public DbSet<NurturingEnrollment> NurturingEnrollments => Set<NurturingEnrollment>();
    public DbSet<SdkVersion> SdkVersions => Set<SdkVersion>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LeadsDbContext).Assembly);
        
        modelBuilder.HasDefaultSchema("leads");

        // Multi-tenant query filters — must stay in DbContext because they
        // capture the `tenant` constructor parameter.
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

        modelBuilder.Entity<LeadSourceConfig>().HasQueryFilter(lc => lc.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<LeadImportJob>().HasQueryFilter(lc => lc.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<LeadSourceRun>().HasQueryFilter(lc => lc.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<LeadIngestion>().HasQueryFilter(lc => lc.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ScoringConfig>().HasQueryFilter(lc => lc.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<TaskTypeConfig>().HasQueryFilter(lc => lc.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<PipelineStageConfig>().HasQueryFilter(lc => lc.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<SlaConfig>().HasQueryFilter(lc => lc.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<NurturingSequence>().HasQueryFilter(s => s.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<NurturingEnrollment>().HasQueryFilter(e => e.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<Opportunity>().HasQueryFilter(o => o.TenantId == tenant.CurrentTenantId);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        optionsBuilder.EnableSensitiveDataLogging();
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning));
    }
}
