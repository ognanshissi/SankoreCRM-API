using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
using Sankore.Modules.Workflow.Infrastructure.Triggers;
using Sankore.Modules.Workflow.PublicApi;

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
        services.AddSingleton<IRuleEvaluator, RuleEvaluator>();
        services.AddSingleton<IConditionEvaluator, ConditionEvaluator>();

        // Action executor pipeline
        services.AddHttpClient("WorkflowWebhook");
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
        services.AddValidatorsFromAssembly(typeof(WorkflowModule).Assembly);

        // SLA deadline checker — runs every minute, times out overdue steps.
        services.AddHostedService<SlaCheckerJob>();

        // Schedule trigger job (stub — full cron scheduling is a future phase).
        services.AddHostedService<WorkflowScheduleTriggerJob>();

        return services;
    }

    /// <summary>
    /// Runs EF migrations. Call once at startup inside a scoped block in Program.cs.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<WorkflowDbContext>();
        await db.Database.MigrateAsync();
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
