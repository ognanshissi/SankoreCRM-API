using Sankore.Modules.Leads.Features.Reports.GetAgentReport;
using Sankore.Modules.Leads.Features.Dispatching.GetDispatchOverview;
using Sankore.Modules.Leads.Features.Reports.GetLeadReport;
using Microsoft.AspNetCore.Builder;

namespace Sankore.Modules.Leads;

using Sankore.Modules.Leads.Features.Ingestion;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.CloseLead;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.PreviewDispatch;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Features.GetLead;
using Sankore.Modules.Leads.Features.ConvertLead;
using Sankore.Modules.Leads.Features.CustomerLink;
using Sankore.Modules.Leads.Features.Fatca;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Enrichment;
using Sankore.Modules.Leads.Features.Questionnaires;
using Sankore.Modules.Leads.Features.Bulk;
using Sankore.Modules.Leads.Features.Import;
using Sankore.Shared.Infrastructure.Egress;
using Sankore.Shared.Infrastructure.FileStore;
using Sankore.Shared.Infrastructure.Google;
using Sankore.Modules.Leads.Features.Import.ValidateImport;
using Sankore.Modules.Leads.Features.DismissDuplicate;
using Sankore.Modules.Leads.Features.ListDismissals;
using Sankore.Modules.Leads.Features.RecordConsent;
using Sankore.Modules.Leads.Features.WithdrawConsent;
using Sankore.Modules.Leads.Features.ListConsents;
using Sankore.Modules.Leads.Features.QualificationTemplates;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.MergeLeads;
using Sankore.Modules.Leads.Features.GetLeadTimeline;
using Sankore.Modules.Leads.Features.ExportLeads;
using Sankore.Modules.Leads.Features.GetSlaBreaches;
using Sankore.Modules.Leads.Features.GetAgentPerformance;
using Sankore.Modules.Leads.Features.GetMyPerformance;
using Sankore.Modules.Leads.Features.GetMyAgenda;
using Sankore.Modules.Leads.Features.Appointments;
using Sankore.Modules.Leads.Features.WhatsApp;
using Sankore.Modules.Leads.Features.WhatsApp.Bot;
using Sankore.Modules.Leads.Features.GetFunnelMetrics;
using Sankore.Modules.Leads.Features.DispatchingRules;
using Sankore.Modules.Leads.Features.GetAssignmentHistory;
using Sankore.Modules.Leads.Features.Settings;
using Sankore.Modules.Leads.Features.RecordFirstContact;
using Sankore.Modules.Leads.Features.Reminders;
using Sankore.Modules.Leads.Features.Tags;
using Sankore.Modules.Leads.Features.GetActivity;
using Sankore.Modules.Leads.Features.GetLeadStats;
using Sankore.Modules.Leads.Features.NurtureLead;
using Sankore.Modules.Leads.Features.RecycleLead;
using Sankore.Modules.Leads.Features.ReopenLead;
using Sankore.Modules.Leads.Features.ReturnLeadToQueue;
using Sankore.Modules.Leads.Features.GetOwnerHistory;
using Sankore.Modules.Leads.Features.GetScoreHistory;
using Sankore.Modules.Leads.Features.NextAction;
using Sankore.Modules.Leads.Features.RecalculateLeadScore;
using Sankore.Modules.Leads.Features.Tasks;
using Sankore.Modules.Leads.Features.TaskGenerationRules;
using Sankore.Modules.Leads.Features.LeadSources;
using Sankore.Modules.Leads.Features.ScoringConfigs;
using Sankore.Modules.Leads.Features.TaskTypes;
using Sankore.Modules.Leads.Features.PipelineStages;
using Sankore.Modules.Leads.Features.SlaConfigs;
using Sankore.Modules.Leads.Features.GetPipeline;
using Sankore.Modules.Leads.Features.Opportunities;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.SlaMonitoring;
using Sankore.Modules.Leads.Features.NurturingExecution;
using Sankore.Modules.Leads.Features.Ingestion.Pull;
using Sankore.Modules.Leads.Features.Ingestion.Web;
using Sankore.Modules.Leads.Features.Ingestion.Webhook;
using Sankore.Modules.Leads.Features.Ingestions;
using Sankore.Modules.Leads.Features.LeadSources.Sdk;
using Sankore.Modules.Leads.Features.LeadSources.ProviderDoc;
using Sankore.Modules.Leads.Features.LeadSources.Snippet;
using Sankore.Modules.Leads.Features.Ingestions.ReplayIngestion;
using Sankore.Modules.Leads.Features.ReactivateRecycledLeads;
using Sankore.Modules.Leads.Features.ListActivities;
using Sankore.Modules.Leads.Features.ListLeads;
using Sankore.Modules.Leads.Features.LogActivity;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Features.SetIntentLevel;
using Sankore.Modules.Leads.Features.SetQualificationCompleteness;
using Sankore.Modules.Leads.Features.UpdateLead;
using Sankore.Modules.Leads.Features.UpdateLeadOwner;
using Sankore.Modules.Leads.Features.SystemCaptureLead;
using Sankore.Modules.Leads.Features.UpdatePipelineStage;
using Sankore.Modules.Leads.Features.NurturingSequences;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.Workflow;

public static class LeadsModule
{
    public static IServiceCollection AddLeadsModule(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<LeadsDbContext>(opt =>
            opt.UseNpgsql(
                config.GetConnectionString("Database"),
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "leads"))
                .UseSnakeCaseNamingConvention());

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(LeadsModule).Assembly));

        // includeInternalTypes: validators here are mostly `internal sealed`, and the
        // assembly scan skips those by default — without this they never run.
        services.AddValidatorsFromAssembly(typeof(LeadsModule).Assembly, includeInternalTypes: true);

        // Assigned-only reads: an Agent or a CommercialAgent sees the leads they hold.
        services.AddScoped<Features.Visibility.LeadVisibility>();
        services.AddScoped<Features.ProjectTemplates.ProjectTemplateResolver>();
        // Writes to the person of a lead or project when a workflow « Notification » step asks.
        services.AddScoped<Features.WorkflowNotifications.LeadWorkflowNotifier>();
        // The CRM task of a workflow « Tâche manuelle » step.
        services.AddScoped<Features.WorkflowTasks.LeadWorkflowTasks>();

        // Nightly technical purge (TechnicalPurgeJob).
        services.AddScoped<Sankore.Shared.Infrastructure.Retention.ITechnicalPurge, Infrastructure.Retention.LeadImportFilesPurge>();
        services.AddScoped<Sankore.Shared.Infrastructure.Retention.ITechnicalPurge, Infrastructure.Retention.RawIngestionsPurge>();
        services.AddScoped<Sankore.Shared.Infrastructure.Retention.ITechnicalPurge, Infrastructure.Retention.RawExternalEventsPurge>();
        services.AddScoped<Features.Ingestion.Webhook.WebhookSourceGate>();
        // Emptying a TEST institution (data-purge lot 4).
        services.AddScoped<Sankore.Shared.Infrastructure.Retention.ITenantDataWipe, Infrastructure.Retention.LeadsTenantDataWipe>();
        services.AddScoped<Features.ProjectTemplates.ProjectStepGate>();
        services.AddScoped<Features.ProjectTemplates.ProjectWorkflows>();
        services.AddScoped<Features.ProjectTemplates.ProjectStepTransition>();
        services.AddScoped<Features.Opportunities.History.ProjectHistoryRecorder>();
        services.AddScoped<Features.ProjectTemplates.ProjectLeadConversion>();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(Features.Visibility.LeadVisibilityBehavior<,>));

        // DispatchLead slice-internal services
        services.AddScoped<CompatibilityScorer>();
        // AgentCapacityService: Redis-cached open CRM task count per agent (US-M13-082).
        // IDistributedCache is null in unit tests → falls back to direct DB query.
        services.AddScoped(sp =>
            new AgentCapacityService(
                sp.GetRequiredService<LeadsDbContext>(),
                sp.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>()));
        // CompatibilityScoringStrategy receives both caches when registered.
        services.AddScoped(sp =>
            new CompatibilityScoringStrategy(
                sp.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
                sp.GetRequiredService<AgentCapacityService>()));
        services.AddScoped<RoundRobinStrategy>();
        services.AddScoped<WeightedRoundRobinStrategy>();
        services.AddScoped<StickyAssignmentStrategy>();
        services.AddScoped<CherryPickingStrategy>();
        services.AddScoped<DispatchingStrategyFactory>();
        // Client-history criterion (F13.10 / BNF-B-22).
        services.AddScoped<ClientAdvisorResolver>();
        // AgentExclusionService: Redis-cached per-task decline exclusions (US-M13-084).
        services.AddScoped(sp =>
            new AgentExclusionService(
                sp.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>()));

        // Phone blind index (HMAC-SHA256) for duplicate detection (US-M13-010).
        // Predates LeadsFieldProtection and stays: its indexes are already stored and F13.2 reads
        // them — recomputing them under another key would stop every phone match from resolving.
        services.AddSingleton<IPhoneBlindIndexer, HmacPhoneBlindIndexer>();

        // Field protection, KEYED to this module. Never AddFieldProtection(config, "Leads") —
        // see LeadsFieldProtection for why that would hand M01's data to M13's key.
        services.AddLeadsFieldProtection(config);

        // E-mail blind index over the keyed indexer above — the second dedup key of F13.2
        // (BNF-B-01). Scoped, not singleton: it resolves a keyed service and has no state.
        services.AddScoped<ILeadEmailIndexer, LeadEmailIndexer>();

        // Household and PEP (BNF-B-03, BNF-B-05). LeadAccessGuard and HouseholdPolicyResolver are
        // the two gates every household write runs; HouseholdWriteContext sequences them.
        services.AddScoped<LeadAccessGuard>();
        services.AddScoped<HouseholdPolicyResolver>();
        services.AddScoped<HouseholdWriteContext>();

        // Multi-person questionnaires (BNF-B-07/08/09).
        services.AddScoped<Features.Questionnaires.ExpectedResponsePlanner>();
        services.AddScoped<Features.Questionnaires.HealthConsentGuard>();
        services.AddScoped<Features.Questionnaires.QuestionnaireCompletenessChecker>();
        services.AddScoped<Features.Questionnaires.IProjectAnswersSnapshots, Features.Questionnaires.ProjectAnswersSnapshotBuilder>();

        // FATCA (BNF-B-11).
        services.AddScoped<Features.Fatca.FatcaSubjectPlanner>();

        // Completeness (BNF-B-04). ONE calculator, read by the completeness endpoint, by the
        // enrichment response and by the conversion gate — see LeadCompletenessCalculator.
        services.AddScoped<Features.Enrichment.LeadCompletenessCalculator>();

        // QualifyLead slice-internal services
        services.AddScoped<LeadScoreCalculator>();
        services.AddScoped<Features.DispatchLead.DispatchingRuleResolver>();

        // Per-tenant M13 parameters (leads.lead_settings). Distinct from the `Leads`
        // configuration section, which is per deployment — see LeadSetting for why both exist.
        services.AddScoped<ILeadSettings, LeadSettingsService>();
        // GetMyPerformance borne ses périodes sur l'horloge injectée. TryAdd : d'autres modules
        // l'enregistrent aussi, et l'ordre de chargement ne doit rien changer.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IContextProvider, LeadContextProvider>();
        services.AddScoped<IContextProvider, ProjectContextProvider>();
        // What workflow « Formulaire », « Formulaire de qualification » and « Vérification KYC » steps ask of a lead or a project.
        services.AddScoped<IWorkflowRecordGate, LeadRecordGate>();
        services.AddScoped<IWorkflowRecordGate, ProjectRecordGate>();
        // Public contract of M13. Consumed by the Customers module (M01) to import a
        // converted lead's commercial history into the client timeline (F13.29).
        services.AddScoped<PublicApi.ILeadsModule, LeadsModuleFacade>();

        // WhatsApp (WA lot 1): the read guard shared by the thread's slices, and the catalogue
        // synchroniser shared by the hourly job and the « Synchroniser » button.
        services.AddScoped<Features.WhatsApp.WhatsAppLeadAccess>();
        services.AddScoped<Features.WhatsApp.WhatsAppTemplateSynchronizer>();
        services.AddScoped<Features.WhatsApp.SyncWhatsAppTemplatesJob>();
        services.AddScoped<Features.WhatsApp.WhatsAppConversationLoader>();
        services.AddScoped<Features.WhatsApp.WhatsAppTakeoverJob>();
        services.AddScoped<Features.WhatsApp.WhatsAppEscalation>();
        // Complaints taken on WhatsApp, question by question, no model (2026-10-26).
        services.AddScoped<Features.WhatsApp.Complaints.WhatsAppComplaintDesk>();

        // The WhatsApp bot (WA lot 3). The model is chosen per DEPLOYMENT: Anthropic with a key,
        // the scripted fake in Development, otherwise none — and « none » is a model that says
        // so, which makes every conversation escalate directly instead of failing on each one.
        services.Configure<Features.WhatsApp.Bot.WhatsAppBotOptions>(config.GetSection(Features.WhatsApp.Bot.WhatsAppBotOptions.Section));
        var bot = config.GetSection(Features.WhatsApp.Bot.WhatsAppBotOptions.Section).Get<Features.WhatsApp.Bot.WhatsAppBotOptions>()
                  ?? new Features.WhatsApp.Bot.WhatsAppBotOptions();

        switch (bot.Provider?.Trim().ToLowerInvariant())
        {
            case "fake":
                services.AddSingleton<Features.WhatsApp.Bot.IWhatsAppBotModel, Features.WhatsApp.Bot.FakeWhatsAppBotModel>();
                break;
            case "anthropic" when !string.IsNullOrWhiteSpace(bot.ApiKey):
                services.AddHttpClient<Features.WhatsApp.Bot.IWhatsAppBotModel, Features.WhatsApp.Bot.AnthropicWhatsAppBotModel>(
                    Features.WhatsApp.Bot.AnthropicWhatsAppBotModel.HttpClientName,
                    // A backstop only: the engine's own token cancels the whole turn at
                    // TimeoutSeconds, every call included.
                    client => client.Timeout = TimeSpan.FromSeconds(Math.Max(5, bot.TimeoutSeconds) + 5));
                break;
            default:
                services.AddSingleton<Features.WhatsApp.Bot.IWhatsAppBotModel, Features.WhatsApp.Bot.UnavailableWhatsAppBotModel>();
                break;
        }

        // WhatsApp media (WA lot 4): a keyed medium for this concern — the host registers a bucket
        // under the same key first when one is configured, this is only the fallback — and the
        // store ONLY when a key exists. Without it, media are fetched from the provider on demand.
        services.Configure<Infrastructure.WhatsAppMedia.WhatsAppMediaStorageOptions>(
            config.GetSection(Infrastructure.WhatsAppMedia.WhatsAppMediaStorageOptions.SectionName));

        services.TryAddKeyedSingleton<Sankore.Shared.ObjectStorage.IObjectBackend>(
            Infrastructure.WhatsAppMedia.WhatsAppMediaStorage.Concern, (sp, _) =>
            {
                var options = sp.GetRequiredService<IOptions<Infrastructure.WhatsAppMedia.WhatsAppMediaStorageOptions>>().Value;
                var root = string.IsNullOrWhiteSpace(options.BasePath)
                    ? Path.Combine(AppContext.BaseDirectory, Infrastructure.WhatsAppMedia.WhatsAppMediaStorage.Concern)
                    : Path.GetFullPath(options.BasePath);
                return new Sankore.Shared.ObjectStorage.LocalObjectBackend(
                    root, sp.GetRequiredService<ILogger<Sankore.Shared.ObjectStorage.LocalObjectBackend>>());
            });

        if (!string.IsNullOrWhiteSpace(config[$"{Infrastructure.WhatsAppMedia.WhatsAppMediaStorageOptions.SectionName}:EncryptionKey"]))
        {
            services.TryAddSingleton<Infrastructure.WhatsAppMedia.IWhatsAppMediaStore>(sp =>
                new Infrastructure.WhatsAppMedia.EncryptedWhatsAppMediaStore(
                    sp.GetRequiredKeyedService<Sankore.Shared.ObjectStorage.IObjectBackend>(Infrastructure.WhatsAppMedia.WhatsAppMediaStorage.Concern),
                    sp.GetRequiredService<IOptions<Infrastructure.WhatsAppMedia.WhatsAppMediaStorageOptions>>(),
                    sp.GetRequiredService<ILogger<Infrastructure.WhatsAppMedia.EncryptedWhatsAppMediaStore>>()));
        }

        // Task attachments: same arrangement — a keyed medium (bucket first, local folder as the
        // fallback), and the store ONLY when its own key exists.
        services.Configure<Infrastructure.TaskAttachments.TaskAttachmentStorageOptions>(
            config.GetSection(Infrastructure.TaskAttachments.TaskAttachmentStorageOptions.SectionName));

        services.TryAddKeyedSingleton<Sankore.Shared.ObjectStorage.IObjectBackend>(
            Infrastructure.TaskAttachments.TaskAttachmentStorage.Concern, (sp, _) =>
            {
                var options = sp.GetRequiredService<IOptions<Infrastructure.TaskAttachments.TaskAttachmentStorageOptions>>().Value;
                var root = string.IsNullOrWhiteSpace(options.BasePath)
                    ? Path.Combine(AppContext.BaseDirectory, Infrastructure.TaskAttachments.TaskAttachmentStorage.Concern)
                    : Path.GetFullPath(options.BasePath);
                return new Sankore.Shared.ObjectStorage.LocalObjectBackend(
                    root, sp.GetRequiredService<ILogger<Sankore.Shared.ObjectStorage.LocalObjectBackend>>());
            });

        if (!string.IsNullOrWhiteSpace(config[$"{Infrastructure.TaskAttachments.TaskAttachmentStorageOptions.SectionName}:EncryptionKey"]))
        {
            services.TryAddSingleton<Infrastructure.TaskAttachments.ITaskAttachmentStore>(sp =>
                new Infrastructure.TaskAttachments.EncryptedTaskAttachmentStore(
                    sp.GetRequiredKeyedService<Sankore.Shared.ObjectStorage.IObjectBackend>(Infrastructure.TaskAttachments.TaskAttachmentStorage.Concern),
                    sp.GetRequiredService<IOptions<Infrastructure.TaskAttachments.TaskAttachmentStorageOptions>>(),
                    sp.GetRequiredService<ILogger<Infrastructure.TaskAttachments.EncryptedTaskAttachmentStore>>()));
        }

        // Tasks: who may open one, and what it can be attached to.
        services.AddScoped<Features.Tasks.Collaboration.TaskAccess>();
        services.AddScoped<Features.Tasks.Collaboration.TaskSubjects>();

        services.AddScoped<Features.WhatsApp.WhatsAppMediaReader>();
        services.AddScoped<Features.WhatsApp.WhatsAppPerimeter>();
        services.AddScoped<Features.WhatsApp.WhatsAppAudit>();
        services.AddScoped<Features.WhatsApp.WhatsAppRetentionJob>();

        services.AddScoped<Features.WhatsApp.Bot.WhatsAppBotProfiles>();
        services.AddScoped<Features.WhatsApp.Bot.WhatsAppBotEngine>();
        services.AddScoped<Features.WhatsApp.Bot.WhatsAppBotFieldValues>();
        services.AddScoped<Features.WhatsApp.WhatsAppProspects>();
        services.AddOutboxForModule<LeadsDbContext>();
        services.AddLocalization(opts => opts.ResourcesPath = "Resources");

        services.Configure<LeadModuleSettings>(config.GetSection("Leads"));
        services.Configure<SnippetOptions>(config.GetSection("Leads:Snippet"));

        // SLA monitoring — Hangfire recurring job (US-M13-141/142)
        services.AddSlaMonitoring();

        // Nurturing execution — Hangfire recurring job (US-M13-151)
        services.AddTransient<ExecuteNurturingJob>();

        // Recycled lead reactivation — Hangfire recurring job (US-M13-160/161)
        services.AddTransient<ReactivateRecycledLeadsJob>();
        services.AddTransient<Features.ProjectTemplates.ProjectCloseDateOverdueJob>();
        services.AddTransient<Features.DispatchQueue.DispatchWaitingLeadsJob>();

        // Health questionnaire retention purge — Hangfire recurring job (BNF-B-09)
        services.AddTransient<Features.Questionnaires.PurgeHealthResponsesJob>();
        services.AddTransient<Features.Retention.AnonymizeClosedLeadsJob>();

        // Ingestion replay — Hangfire on-demand job (F13.37-BE-11)
        services.AddTransient<ReplayIngestionJob>();

        // Generic REST puller — SSRF-safe HttpClient with Polly retry (F13.37-BE-19/20)
        services.AddSafeOutboundHttpClient("LeadPuller")
            .AddStandardResilienceHandler(opts =>
            {
                opts.Retry.MaxRetryAttempts = 3;
                opts.Retry.UseJitter = true;
                opts.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                opts.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
                opts.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            });
        services.AddScoped<Features.Ingestion.Pull.GenericRestPuller>();
        services.AddTransient<Features.Ingestion.Pull.LeadSourcePullOrchestratorJob>();
        services.AddTransient<Features.Ingestion.Pull.PullLeadSourceJob>();

        // Web ingest — captcha validator (stub MVP)
        services.AddSingleton<ICaptchaValidator, StubCaptchaValidator>();

        // SDK serve CORS — any origin, because the snippet is pasted on third-party sites and
        // carries crossorigin="anonymous". See SdkEndpoints.PublicCorsPolicy.
        services.AddSdkServeCors();

        // SDK file store — local filesystem, rooted at Leads:SdkStoragePath or <content root>/wwwroot/sdk
        services.AddSingleton<ISdkFileStore>(sp => new LocalSdkFileStore(
            LocalSdkFileStore.ResolveBasePath(
                config, sp.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>())));

        // Import — multi-source (CSV, Excel, Google Sheets, Google Contacts)
        services.AddLocalFileStore();
        services.AddGoogleImportSettings(config);
        services.AddTransient<Features.Import.ProcessLeadImportJob>();
        services.AddTransient<Features.Import.Readers.FileImportReader>();
        services.AddTransient<Features.Import.Readers.GoogleSheetsImportReader>();
        services.AddTransient<Features.Import.Readers.GoogleContactsImportReader>();

        return services;
    }

    /// <summary>
    /// Runs migrations and seeds the SDK builds shipped in wwwroot/sdk.
    /// Called from the startup scope in Program.cs.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<Infrastructure.LeadsDbContext>();
        await db.Database.MigrateAsync();

        var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Infrastructure.LeadsDbContext>>();

        await Features.LeadSources.Sdk.SdkVersionSeeder.SeedAsync(
            db,
            sp.GetRequiredService<Features.LeadSources.Sdk.ISdkFileStore>(),
            logger);

        // Makes the implicit default rule visible and tunable per tenant. Same values, so
        // dispatching behaves exactly as before.
        await Features.DispatchingRules.DispatchingRuleSeeder.SeedAsync(
            db, sp.GetRequiredService<Sankore.Shared.Kernel.ITenantStore>(), logger);

        // One lead_settings row per declared key per tenant. Idempotent: inserts only what is
        // missing, so an operator's change survives a restart.
        await Infrastructure.LeadSettingSeeder.SeedAsync(
            db, sp.GetRequiredService<Sankore.Shared.Kernel.ITenantStore>(), logger);

        // Moves pre-encryption e-mails into email_enc + email_blind_index. Before the host serves
        // its first request on purpose — see LeadEmailProtectionBackfill. Idempotent and resumable,
        // so a completed migration costs one indexed count.
        await Infrastructure.LeadEmailProtectionBackfill.RunAsync(
            db,
            sp.GetRequiredService<Features.FindDuplicates.ILeadEmailIndexer>(),
            logger);
    }

    public static IEndpointRouteBuilder MapLeadsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("leads");

        // Phase 1 — Core CRUD & Lifecycle
        group.MapListLeads();
        group.MapCaptureLead();
        group.MapSystemCaptureLead();
        group.MapGetLead();
        group.MapUpdateLead();
        group.MapUpdateLeadOwner();
        group.MapGetOwnerHistory();
        group.MapUpdatePipelineStage();
        group.MapCloseLead();
        group.MapDispatchLead();
        group.MapPreviewDispatch();

        // Phase 2 — Qualification & Scoring
        group.MapQualifyLead();
        group.MapRecalculateLeadScore();
        group.MapNextActionEndpoints();
        group.MapSetIntentLevel();
        group.MapSetQualificationCompleteness();
        group.MapGetScoreHistory();

        // Phase 3 — Activities & Interactions
        group.MapLogActivity();
        group.MapListActivities();
        group.MapGetActivity();

        // Phase 4 — Conversion
        group.MapConvertLead();

        // Existing-customer link (BNF-B-22)
        group.MapCustomerLinkEndpoints();

        // Household and PEP (BNF-B-03, BNF-B-05)
        group.MapHouseholdEndpoints();

        // Per-person questionnaires and health consent (BNF-B-07/08/09)
        group.MapQuestionnaireEndpoints();

        // FATCA indicia and self-certification (BNF-B-11)
        group.MapFatcaEndpoints();

        // Enrichment and completeness (BNF-B-02, BNF-B-04)
        group.MapEnrichmentEndpoints();

        // Phase 5 — Nurturing, Recycling & Lifecycle
        group.MapGetLeadStats();
        group.MapGetLeadReport();
        group.MapGetAgentReport();
        group.MapGetDispatchOverview();
        group.MapReopenLead();
        group.MapNurtureLead();
        group.MapRecycleLead();
        group.MapReturnLeadToQueue();

        // Phase 6 — Dispatching Rules & Assignment History
        group.MapGetAssignmentHistory();
        group.MapDispatchingRulesEndpoints();

        // Phase 7 — Reminders & First Contact
        group.MapRecordFirstContact();
        group.MapRemindersEndpoints();

        // Phase 8 — Lead Tagging
        group.MapTagsEndpoints();

        // Phase 9 — Bulk Operations
        group.MapBulkEndpoints();

        // Phase 10 — Import, Duplicate Detection & Merge
        group.MapImportLeads();
        group.MapImportLeadsEndpoints();
        group.MapValidateLeadImport();
        group.MapGetImportStatus();
        group.MapFindDuplicates();
        group.MapMergeLeads();
        group.MapDismissDuplicate();
        group.MapListDismissals();

        // Qualification Templates
        group.MapQualificationTemplatesEndpoints();

        // Phase 13 — Consent Management
        group.MapRecordConsent();
        group.MapWithdrawConsent();
        group.MapListConsents();

        // Phase 11 — Lead Timeline & Export
        group.MapGetLeadTimeline();
        group.MapExportLeads();

        // Phase 12 — SLA Monitoring & Agent Performance
        group.MapGetSlaBreaches();
        group.MapGetAgentPerformance();
        group.MapGetMyPerformance();
        group.MapGetMyAgenda();
        group.MapAppointmentsEndpoints();
        group.MapWhatsAppEndpoints();
        group.MapWhatsAppBotEndpoints();
        group.MapGetFunnelMetrics();

        // Phase 13 — CRM Tasks & Task Generation Rules (US-M13-080)
        group.MapTasksEndpoints();
        group.MapTaskGenerationRulesEndpoints();

        // Pipeline Kanban view (US-M13-120)
        group.MapGetPipeline();

        // Opportunities (US-M13-130/131)
        group.MapOpportunitiesEndpoints();
        group.MapProjectTemplatesEndpoints();

        // Nurturing Sequences catalogue (US-M13-150)
        group.MapNurturingSequencesEndpoints();

        // Phase 14 — Configuration (US-M13-190..197)
        group.MapLeadSourcesEndpoints();
        group.MapProviderDocEndpoint();
        group.MapSourceAnalyticsEndpoints();
        group.MapScoringConfigsEndpoints();
        group.MapTaskTypesEndpoints();
        group.MapPipelineStagesEndpoints();
        group.MapSlaConfigsEndpoints();

        // Pull dry-run & manual pull (F13.37-BE-22)
        group.MapPullEndpoints();

        // Ingestions (F13.37-BE-11)
        group.MapIngestionsEndpoints();

        // SDK admin (F13.37-BE-15)
        group.MapSdkAdminEndpoints();

        // On `app`, not on `group`: the tenant parameters are their own resource, at
        // `lead-settings`, matching M01's `customer-settings` and M02's `kyc-settings`. Mapping
        // them inside the group would bury them at `leads/lead-settings`.
        app.MapLeadSettingsEndpoints();

        return app;
    }

    /// <summary>
    /// Maps public (unauthenticated) ingest endpoints outside the api/v1 group.
    /// Called directly from Program.cs BEFORE UseAuthentication.
    /// </summary>
    public static IEndpointRouteBuilder MapPublicIngestEndpoints(this IEndpointRouteBuilder app)
    {
        // An empty-prefix group: every path below stays exactly as written, and the tenant
        // filter is declared ONCE for all of them. These routes are exempt from
        // TenantResolutionMiddleware, so without it the pipeline reaches AuditBehavior with no
        // tenant and throws after the lead has already been ingested.
        var ingest = app.MapGroup(string.Empty).ResolveIngestTenantFromPublicKey();

        ingest.MapWebIngestEndpoint();
        ingest.MapWebIngestCorsEndpoint();
        ingest.MapWebPingEndpoint();
        ingest.MapWebFormEndpoint();
        ingest.MapWebhookIngestEndpoint();
        ingest.MapExternalEventIngestEndpoint();

        // Outside the group on purpose: an SDK file is the same bytes for every tenant, the route
        // carries no public key, and SdkVersion is platform-level with no tenant column.
        app.MapSdkServeEndpoints();

        return app;
    }
}
