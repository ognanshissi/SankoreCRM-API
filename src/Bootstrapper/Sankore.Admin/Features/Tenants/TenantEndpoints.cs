using Sankore.Admin.Features.Tenants.CreateTenant;
using Sankore.Admin.Features.Tenants.DeleteTenant;
using Sankore.Admin.Features.Tenants.GetTenant;
using Sankore.Admin.Features.Tenants.ListTenants;
using Sankore.Admin.Features.Tenants.UpdateTenant;

namespace Sankore.Admin.Features.Tenants;

public static class TenantEndpoints
{
    public static void MapTenantEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tenants")
            .WithTags("Tenants");

        ListTenantsEndpoint.Map(group);
        GetTenantEndpoint.Map(group);
        CreateTenantEndpoint.Map(group);
        UpdateTenantEndpoint.Map(group);
        DeleteTenantEndpoint.Map(group);
    }
}