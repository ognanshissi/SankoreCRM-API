using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Workflow.Features.Instances;
using Sankore.Modules.Workflow.Features.Templates;
using Sankore.Modules.Workflow.Infrastructure;
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

        // MediatR handlers + FluentValidation validators for all Features/* slices
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(WorkflowModule).Assembly));
        services.AddValidatorsFromAssembly(typeof(WorkflowModule).Assembly);

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
        var group = app.MapGroup("workflow").WithTags("Workflow");
        group.MapTemplatesEndpoints();
        group.MapInstancesEndpoints();
        return app;
    }
}
