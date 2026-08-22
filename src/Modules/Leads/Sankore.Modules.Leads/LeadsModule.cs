using Microsoft.AspNetCore.Builder;

namespace Sankore.Modules.Leads;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Extensions;

/// <summary>
/// Composition root of the Leads module (M13). This is the ONE public
/// static class the Bootstrapper touches; every internal type of the
/// module is wired here so adding a new slice never requires editing
/// Program.cs — only this file (one line in MapLeadsEndpoints) and the
/// DI additions for any slice-private services.
/// </summary>
public static class LeadsModule
{
    public static IServiceCollection AddLeadsModule(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<LeadsDbContext>(opt =>
            opt.UseNpgsql(
                config.GetConnectionString("Database"),
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "leads")));

        // MediatR handlers declared anywhere in this assembly (i.e. every
        // Features/* slice) get auto-registered — no per-slice DI wiring needed.
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(LeadsModule).Assembly));

        services.AddValidatorsFromAssembly(typeof(LeadsModule).Assembly);

        // Slice-internal services (CompatibilityScorer is `internal` — only
        // reachable from within this assembly, enforcing the boundary).
        services.AddScoped<CompatibilityScorer>();
        services.AddScoped<CompatibilityScoringStrategy>();
        services.AddScoped<RoundRobinStrategy>();
        services.AddScoped<WeightedRoundRobinStrategy>();
        services.AddScoped<StickyAssignmentStrategy>();
        services.AddScoped<CherryPickingStrategy>();
        services.AddScoped<DispatchingStrategyFactory>();

        services.AddOutboxForModule<LeadsDbContext>();

        return services;
    }

    public static IEndpointRouteBuilder MapLeadsEndpoints(this IEndpointRouteBuilder app)
    {
        return app.MapGroup("leads")
            .MapCaptureLead()
            .MapDispatchLead();
        // Add one line per new slice, e.g.:
        // app.MapQualifyLead();
        // app.MapReassignLead();
        // app.MapConvertLeadToCustomer();
        // app.MapGetLeadPipeline();
        
    }
}
