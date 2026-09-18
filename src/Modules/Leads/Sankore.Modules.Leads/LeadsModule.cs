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
using Sankore.Modules.Leads.Features.GetScoreHistory;
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
        services.AddScoped<CompatibilityScoringStrategy>();
        services.AddScoped<RoundRobinStrategy>();
        services.AddScoped<WeightedRoundRobinStrategy>();
        services.AddScoped<StickyAssignmentStrategy>();
        services.AddScoped<CherryPickingStrategy>();
        services.AddScoped<DispatchingStrategyFactory>();

        // QualifyLead slice-internal services
        services.AddScoped<LeadScoreCalculator>();

        services.AddScoped<IContextProvider, LeadContextProvider>();
        services.AddOutboxForModule<LeadsDbContext>();
        services.AddLocalization(opts => opts.ResourcesPath = "Resources");

        services.Configure<LeadModuleSettings>(config.GetSection("Leads"));

        return services;
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
        group.MapUpdatePipelineStage();
        group.MapCloseLead();
        group.MapDispatchLead();

        // Phase 2 — Qualification & Scoring
        group.MapQualifyLead();
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

        return app;
    }
}
