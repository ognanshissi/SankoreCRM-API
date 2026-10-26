namespace Sankore.Modules.Leads.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;
using Sankore.Shared.Infrastructure.Persistence;

/// <param name="encryptor">
/// M13's OWN encryptor, resolved by DI key. It is a constructor dependency because
/// <c>Lead.Email</c> is protected by an EF value converter, and converters are built while the
/// model is built — see <c>LeadConfiguration</c>. The key is M13's: a lead's e-mail must not be
/// decryptable with M01's or M02's.
/// </param>
public sealed class LeadsDbContext(
    DbContextOptions<LeadsDbContext> options,
    ITenantContext tenant,
    [FromKeyedServices(LeadsFieldProtection.Key)] IFieldEncryptor encryptor)
    : DbContext(options)
{
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadAssignment> LeadAssignments => Set<LeadAssignment>();
    public DbSet<DispatchingRule> DispatchingRules => Set<DispatchingRule>();
    public DbSet<ScoreHistory> ScoreHistories => Set<ScoreHistory>();
    public DbSet<LeadActivity> LeadActivities => Set<LeadActivity>();
    public DbSet<LeadReminder> LeadReminders => Set<LeadReminder>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<WhatsAppConversation> WhatsAppConversations => Set<WhatsAppConversation>();
    public DbSet<WhatsAppMessage> WhatsAppMessages => Set<WhatsAppMessage>();
    public DbSet<WhatsAppTemplate> WhatsAppTemplates => Set<WhatsAppTemplate>();
    public DbSet<WhatsAppBotProfile> WhatsAppBotProfiles => Set<WhatsAppBotProfile>();
    public DbSet<WhatsAppBotTurn> WhatsAppBotTurns => Set<WhatsAppBotTurn>();
    public DbSet<WhatsAppComplaintIntake> WhatsAppComplaintIntakes => Set<WhatsAppComplaintIntake>();
    public DbSet<WhatsAppHandover> WhatsAppHandovers => Set<WhatsAppHandover>();
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
    public DbSet<CrmTaskParticipant> CrmTaskParticipants => Set<CrmTaskParticipant>();
    public DbSet<CrmTaskCriterion> CrmTaskCriteria => Set<CrmTaskCriterion>();
    public DbSet<CrmTaskComment> CrmTaskComments => Set<CrmTaskComment>();
    public DbSet<CrmTaskTag> CrmTaskTags => Set<CrmTaskTag>();
    public DbSet<CrmTaskAttachment> CrmTaskAttachments => Set<CrmTaskAttachment>();
    public DbSet<ProjectAttachment> ProjectAttachments => Set<ProjectAttachment>();
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
    public DbSet<ProjectTemplate> ProjectTemplates => Set<ProjectTemplate>();
    public DbSet<NurturingSequence> NurturingSequences => Set<NurturingSequence>();
    public DbSet<NurturingStep> NurturingSteps => Set<NurturingStep>();
    public DbSet<NurturingEnrollment> NurturingEnrollments => Set<NurturingEnrollment>();
    public DbSet<SdkVersion> SdkVersions => Set<SdkVersion>();
    public DbSet<LeadHouseholdMember> LeadHouseholdMembers => Set<LeadHouseholdMember>();
    public DbSet<FatcaDeclaration> FatcaDeclarations => Set<FatcaDeclaration>();
    public DbSet<LeadSetting> LeadSettings => Set<LeadSetting>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Every timestamp column stores UTC — Npgsql rejects any other offset outright.
    /// See <see cref="UtcTimestamps"/>.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        base.ConfigureConventions(builder);
        builder.UseUtcDateTimeOffsets();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // LeadConfiguration is applied BY HAND and excluded from the scan, because it is the one
        // configuration in this module that needs a dependency: the encrypting value converter on
        // Lead.Email. A scanned IEntityTypeConfiguration is instantiated by EF through its
        // parameterless constructor, so there is no way to hand it anything — and the alternatives
        // (a static field, a service locator) would make which key built the converter depend on
        // whichever context happened to build the model first.
        modelBuilder.ApplyConfiguration(new Configurations.LeadConfiguration(encryptor));
        modelBuilder.ApplyConfiguration(new Configurations.LeadHouseholdMemberConfiguration(encryptor));
        modelBuilder.ApplyConfiguration(new Configurations.QualificationResponseConfiguration(encryptor));
        modelBuilder.ApplyConfiguration(new Configurations.FatcaDeclarationConfiguration(encryptor));
        modelBuilder.ApplyConfiguration(new Configurations.WhatsAppConversationConfiguration(encryptor));
        modelBuilder.ApplyConfiguration(new Configurations.WhatsAppMessageConfiguration(encryptor));
        modelBuilder.ApplyConfiguration(new Configurations.WhatsAppBotTurnConfiguration(encryptor));
        modelBuilder.ApplyConfiguration(new Configurations.WhatsAppComplaintIntakeConfiguration(encryptor));
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(LeadsDbContext).Assembly,
            type => type != typeof(Configurations.LeadConfiguration)
                 && type != typeof(Configurations.WhatsAppConversationConfiguration)
                 && type != typeof(Configurations.WhatsAppMessageConfiguration)
                 && type != typeof(Configurations.WhatsAppBotTurnConfiguration)
                 && type != typeof(Configurations.WhatsAppComplaintIntakeConfiguration)
                 && type != typeof(Configurations.LeadHouseholdMemberConfiguration)
                 && type != typeof(Configurations.QualificationResponseConfiguration)
                 && type != typeof(Configurations.FatcaDeclarationConfiguration));
        
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

        modelBuilder.Entity<Appointment>()
            .HasQueryFilter(a => a.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<WhatsAppConversation>()
            .HasQueryFilter(c => c.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<WhatsAppMessage>()
            .HasQueryFilter(m => m.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<WhatsAppTemplate>()
            .HasQueryFilter(t => t.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<WhatsAppBotProfile>()
            .HasQueryFilter(p => p.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<WhatsAppBotTurn>()
            .HasQueryFilter(t => t.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<WhatsAppComplaintIntake>()
            .HasQueryFilter(i => i.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<WhatsAppHandover>()
            .HasQueryFilter(h => h.TenantId == tenant.CurrentTenantId);

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

        modelBuilder.Entity<CrmTaskParticipant>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<CrmTaskCriterion>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<CrmTaskComment>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<CrmTaskTag>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<CrmTaskAttachment>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ProjectAttachment>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
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
        modelBuilder.Entity<ProjectTemplate>().HasQueryFilter(t => t.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<LeadSetting>().HasQueryFilter(s => s.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<LeadHouseholdMember>().HasQueryFilter(m => m.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<FatcaDeclaration>().HasQueryFilter(d => d.TenantId == tenant.CurrentTenantId);
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
