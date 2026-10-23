using Sankore.Admin.Domain;
using Sankore.Admin.Features.Modules.PlatformSide;
using Sankore.Admin.Features.Modules.TenantSide;

namespace Sankore.Admin.Features.Modules;

/// <summary>
/// The paid modules of a tenant. Three groups, by audience:
/// <list type="bullet">
/// <item><c>/api/v1/tenants/{tenantId}/modules</c> — what Sankore.Api calls with the service key on
/// a tenant administrator's behalf (list, request, cancel, deactivate), plus the platform's direct
/// activation, which carries <c>RequirePlatformAdmin</c>;</item>
/// <item><c>/api/v1/module-subscriptions</c> — the platform's queue and decisions, all
/// <c>RequirePlatformAdmin</c>;</item>
/// <item><c>/api/v1/module-catalog</c> — the offer, readable with either credential, changed by a
/// platform administrator only.</item>
/// </list>
/// </summary>
public static class ModuleEndpoints
{
    public static void MapModuleEndpoints(this WebApplication app)
    {
        var tenantModules = app.MapGroup("/api/v1/tenants/{tenantId:guid}/modules")
            .WithTags("Tenant modules");

        ListTenantModulesEndpoint.Map(tenantModules);
        RequestTenantModuleEndpoint.Map(tenantModules);
        CancelTenantModuleRequestEndpoint.Map(tenantModules);
        DeactivateTenantModuleEndpoint.Map(tenantModules);
        ActivateTenantModuleEndpoint.Map(tenantModules);

        var subscriptions = app.MapGroup("/api/v1/module-subscriptions")
            .WithTags("Module subscriptions");

        ListModuleSubscriptionsEndpoint.Map(subscriptions);
        DecideModuleRequestEndpoints.Map(subscriptions);

        var catalog = app.MapGroup("/api/v1/module-catalog")
            .WithTags("Module catalog");

        ModuleCatalogEndpoints.Map(catalog);

        app.MapGet("/api/v1/tenant-profiles", () => Results.Ok(TenantProfiles.All))
            .WithTags("Module catalog")
            .WithName("ListTenantProfiles")
            .WithSummary("The creation profiles: which paid modules a new tenant starts with")
            .Produces<IReadOnlyList<TenantProfile>>()
            .WithOpenApi();
    }
}
