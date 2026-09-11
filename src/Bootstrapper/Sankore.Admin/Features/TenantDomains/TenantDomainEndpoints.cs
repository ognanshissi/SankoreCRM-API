using Sankore.Admin.Features.TenantDomains.CreateTenantDomain;
using Sankore.Admin.Features.TenantDomains.DeactivateTenantDomain;
using Sankore.Admin.Features.TenantDomains.ListTenantDomainsByTenant;

namespace Sankore.Admin.Features.TenantDomains;

public static class TenantDomainEndpoints
{
    public static void MapTenantDomainEndpoints(this WebApplication app)
    {
        // /api/v1/tenant-domains — standalone CRUD
        var domainsGroup = app.MapGroup("/api/v1/tenant-domains")
            .WithTags("TenantDomains");

        CreateTenantDomainEndpoint.Map(domainsGroup);
        DeactivateTenantDomainEndpoint.Map(domainsGroup);

        // /api/v1/tenants/{tenantId}/domains — nested read under tenant
        var tenantsGroup = app.MapGroup("/api/v1/tenants")
            .WithTags("TenantDomains");

        ListTenantDomainsByTenantEndpoint.Map(tenantsGroup);
    }
}