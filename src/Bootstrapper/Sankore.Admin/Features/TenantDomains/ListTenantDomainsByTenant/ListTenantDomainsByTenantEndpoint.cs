using MediatR;
using Sankore.Admin.Features.TenantDomains;

namespace Sankore.Admin.Features.TenantDomains.ListTenantDomainsByTenant;

public static class ListTenantDomainsByTenantEndpoint
{
    public static void Map(RouteGroupBuilder tenantsGroup)
    {
        tenantsGroup.MapGet("/{tenantId:guid}/domains",
            async (Guid tenantId, IMediator mediator, CancellationToken ct) =>
            {
                var domains = await mediator.Send(new ListTenantDomainsByTenantQuery(tenantId), ct);
                return Results.Ok(domains);
            })
            .WithName("ListTenantDomainsByTenant")
            .WithSummary("List all domains assigned to a tenant")
            .Produces<List<TenantDomainResponse>>(StatusCodes.Status200OK)
            .WithOpenApi();
    }
}