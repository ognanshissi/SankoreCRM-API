using Microsoft.AspNetCore.Builder;

namespace Sankore.Modules.Leads;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.CloseLead;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Features.GetLead;
using Sankore.Modules.Leads.Features.ConvertLead;
using Sankore.Modules.Leads.Features.Bulk;
using Sankore.Modules.Leads.Features.Import;
using Sankore.Modules.Leads.Features.FindDuplicates;
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
using Sankore.Modules.Leads.Features.GetFunnelMetrics;
using Sankore.Modules.Leads.Features.DispatchingRules;
using Sankore.Modules.Leads.Features.GetAssignmentHistory;
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

        services.AddValidatorsFromAssembly(typeof(LeadsModule).Assembly);

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
        // AgentExclusionService: Redis-cached per-task decline exclusions (US-M13-084).
        services.AddScoped(sp =>
            new AgentExclusionService(
                sp.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>()));

        // Phone blind index (HMAC-SHA256) for duplicate detection (US-M13-010)
        services.AddSingleton<IPhoneBlindIndexer, HmacPhoneBlindIndexer>();

        // QualifyLead slice-internal services
        services.AddScoped<LeadScoreCalculator>();

        services.AddScoped<IContextProvider, LeadContextProvider>();
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

        // Ingestion replay — Hangfire on-demand job (F13.37-BE-11)
        services.AddTransient<ReplayIngestionJob>();

        // Generic REST puller — SSRF-safe HttpClient with Polly retry (F13.37-BE-19/20)
        services.AddTransient<Infrastructure.SsrfProtection.SafeRedirectHandler>();
        services.AddHttpClient("LeadPuller")
            .ConfigurePrimaryHttpMessageHandler(() =>
                Infrastructure.SsrfProtection.SsrfSafeHandler.Create())
            .AddHttpMessageHandler<Infrastructure.SsrfProtection.SafeRedirectHandler>()
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

        // SDK file store — local filesystem, rooted at Leads:SdkStoragePath or <content root>/wwwroot/sdk
        services.AddSingleton<ISdkFileStore>(sp => new LocalSdkFileStore(
            LocalSdkFileStore.ResolveBasePath(
                config, sp.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>())));

        // Import — file storage for uploaded CSV files
        services.AddSingleton<IImportFileStore, LocalImportFileStore>();

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

        await Features.LeadSources.Sdk.SdkVersionSeeder.SeedAsync(
            db,
            sp.GetRequiredService<Features.LeadSources.Sdk.ISdkFileStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Infrastructure.LeadsDbContext>>());
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

        // Phase 5 — Nurturing, Recycling & Lifecycle
        group.MapGetLeadStats();
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
        group.MapGetFunnelMetrics();

        // Phase 13 — CRM Tasks & Task Generation Rules (US-M13-080)
        group.MapTasksEndpoints();
        group.MapTaskGenerationRulesEndpoints();

        // Pipeline Kanban view (US-M13-120)
        group.MapGetPipeline();

        // Opportunities (US-M13-130/131)
        group.MapOpportunitiesEndpoints();

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

        return app;
    }

    /// <summary>
    /// Maps public (unauthenticated) ingest endpoints outside the api/v1 group.
    /// Called directly from Program.cs BEFORE UseAuthentication.
    /// </summary>
    public static IEndpointRouteBuilder MapPublicIngestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapWebIngestEndpoint();
        app.MapWebIngestCorsEndpoint();
        app.MapWebPingEndpoint();
        app.MapWebFormEndpoint();
        app.MapWebhookIngestEndpoint();
        app.MapSdkServeEndpoints();
        return app;
    }
}
