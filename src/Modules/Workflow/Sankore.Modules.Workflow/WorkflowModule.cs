using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Features.Analytics;
using Sankore.Modules.Workflow.Features.Instances;
using Sankore.Modules.Workflow.Features.Tasks;
using Sankore.Modules.Workflow.Features.Templates;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure.Jobs;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.Infrastructure.Rules;
using Sankore.Modules.Workflow.Infrastructure.Actions;
using Sankore.Modules.Workflow.Infrastructure.Actions.Executors;
using Sankore.Modules.Workflow.Infrastructure.Consumers;
using Sankore.Modules.Workflow.Infrastructure.Seeders;
using Sankore.Modules.Workflow.Infrastructure.Triggers;
using Sankore.Modules.Workflow.Infrastructure.Notifications;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Egress;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow;

/// <summary>
/// Composition root of the Workflow module. The ONE public static class
/// the Bootstrapper calls — everything else is internal.
/// </summary>
public static class WorkflowModule
{
    public static IServiceCollection AddWorkflowModule(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddDbContext<WorkflowDbContext>(opt =>
            opt.UseNpgsql(
                config.GetConnectionString("Database"),
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "workflow"))
                .UseSnakeCaseNamingConvention());

        services.AddScoped<IWorkflowModule, WorkflowModuleFacade>();
        // Emptying a TEST institution (data-purge lot 4).
        services.AddScoped<Sankore.Shared.Infrastructure.Retention.ITenantDataWipe, Infrastructure.Retention.WorkflowTenantDataWipe>();
        services.AddSingleton<IRuleEvaluator, RuleEvaluator>();
        services.AddSingleton<IConditionEvaluator, ConditionEvaluator>();

        // Action executor pipeline
        services.AddSafeOutboundHttpClient(CallWebhookExecutor.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<IActionExecutor, SendNotificationExecutor>();
        services.AddScoped<IActionExecutor, PublishEventExecutor>();
        services.AddScoped<IActionExecutor, CallWebhookExecutor>();
        services.AddScoped<IActionExecutor, AssignUserExecutor>();
        services.AddScoped<IActionExecutor, AssignRoundRobinExecutor>();
        services.AddScoped<IActionExecutor, CreateTaskExecutor>();
        services.AddScoped<IActionExecutor, StartChildWorkflowExecutor>();
        services.AddScoped<IActionExecutorDispatcher, ActionExecutorDispatcher>();

        // MediatR handlers + FluentValidation validators for all Features/* slices
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(WorkflowModule).Assembly));
        // includeInternalTypes: validators here are mostly `internal sealed`, and the
        // assembly scan skips those by default — without this they never run.
        services.AddValidatorsFromAssembly(typeof(WorkflowModule).Assembly, includeInternalTypes: true);

        // SLA deadline checker — runs every minute, times out overdue steps.
        services.AddHostedService<SlaCheckerJob>();

        // Schedule trigger job (stub — full cron scheduling is a future phase).
        services.AddHostedService<WorkflowScheduleTriggerJob>();

        // The table existed with nothing reading it. It now carries the notification requests
        // WorkflowDbContext writes when an instance franchit a « Notification » step.
        services.AddOutboxForModule<WorkflowDbContext>();

        // Once per start: releases instances stuck on a notification step opened before the
        // engine ran them (they waited for an approval nobody was asked for).
        services.AddHostedService<AutomaticStepsCatchUp>();

        // Tells a record's agent and role holders in-app when a « Notification » step is reached.
        services.AddScoped<InternalWorkflowNotifier>();

        return services;
    }

    /// <summary>
    /// Runs EF migrations, then seeds the built-in templates other modules resolve by entity type.
    /// Call once at startup inside a scoped block in Program.cs.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<WorkflowDbContext>();
        await db.Database.MigrateAsync();

        await WorkflowTemplateSeeder.SeedAsync(
            db,
            sp.GetRequiredService<ITenantStore>(),
            sp.GetRequiredService<ILogger<WorkflowDbContext>>());
    }

    public static IEndpointRouteBuilder MapWorkflowModuleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("workflow");
        group.MapTemplatesEndpoints();
        group.MapInstancesEndpoints();
        group.MapTasksEndpoints();
        group.MapAnalyticsEndpoints();
        return app;
    }
}
