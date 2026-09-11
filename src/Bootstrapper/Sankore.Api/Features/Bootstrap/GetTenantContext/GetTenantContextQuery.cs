using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Api.Features.Bootstrap.GetTenantContext;

public record GetTenantContextQuery(string Fqdn) : IRequest<TenantContextResponse?>;

internal sealed class GetTenantContextHandler(ITenantStore tenantStore)
    : IRequestHandler<GetTenantContextQuery, TenantContextResponse?>
{
    public async Task<TenantContextResponse?> Handle(
        GetTenantContextQuery request, CancellationToken ct)
    {
        var tenant = await tenantStore.GetByFqdnAsync(
            request.Fqdn, ct);

        if (tenant is null)
            return null;

        return new TenantContextResponse(
            TenantId: tenant.Id,
            Name: tenant.Name,
            IsActive: tenant.IsActive,
            IsMaintenance: tenant.IsMaintenance,
            TrialExpiresAt: tenant.TrialExpiresAt);
    }
}
