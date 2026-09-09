using MediatR;
using Sankore.Admin.Features.Tenants.GetTenant;

namespace Sankore.Admin.Features.Tenants.ListTenants;

public static class ListTenantsEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IMediator mediator, CancellationToken ct) =>
            {
                var tenants = await mediator.Send(new ListTenantsQuery(), ct);
                return Results.Ok(tenants);
            })
            .WithName("ListTenants")
            .WithSummary("List all tenants")
            .Produces<List<TenantResponse>>();
    }
}
