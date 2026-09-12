using MediatR;
using Sankore.Modules.Administration.Features.CompanyInfo.GetCompanyInfo;
using Sankore.Shared.Kernel;

namespace Sankore.Api.Features.Bootstrap.GetTenantContext;

public record GetTenantContextQuery(string Fqdn) : IRequest<TenantContextResponse?>;

internal sealed class GetTenantContextHandler(ITenantStore tenantStore, ISender sender)
    : IRequestHandler<GetTenantContextQuery, TenantContextResponse?>
{
    public async Task<TenantContextResponse?> Handle(
        GetTenantContextQuery request, CancellationToken ct)
    {
        var tenant = await tenantStore.GetByFqdnAsync(request.Fqdn, ct);
        if (tenant is null)
            return null;

        var branding = await sender.Send(new GetCompanyInfoQuery(tenant.Id), ct);

        return new TenantContextResponse(
            TenantId: tenant.Id,
            Name: tenant.Name,
            IsActive: tenant.IsActive,
            IsMaintenance: tenant.IsMaintenance,
            TrialExpiresAt: tenant.TrialExpiresAt,
            CompanyName: branding?.Name,
            Description: branding?.Description,
            LogoUrl: branding?.LogoUrl,
            PrimaryColor: branding?.PrimaryColor,
            SecondaryColor: branding?.SecondaryColor,
            DefaultLanguage: branding?.DefaultLanguage);
    }
}
