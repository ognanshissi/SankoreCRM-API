using MediatR;
using Microsoft.AspNetCore.Mvc;
using Sankore.Admin.Features.Tenants.GetTenant;

namespace Sankore.Admin.Features.Tenants.ListTenants;

public static class ListTenantsEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IMediator mediator, [FromQuery] bool? activeOnly, CancellationToken ct) =>
            {
                var tenants = await mediator.Send(new ListTenantsQuery(activeOnly), ct);
                return Results.Ok(tenants);
            })
            .WithName("ListTenants")
            .WithSummary("List all tenants")
            .Produces<List<TenantResponse>>()
            .WithOpenApi();
    }
    
    public record ListTenantsQueryRequest(bool? ActiveOnly);
}
