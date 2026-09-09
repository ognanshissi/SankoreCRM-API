using MediatR;
using Sankore.Admin.Features.Tenants.GetTenant;

namespace Sankore.Admin.Features.Tenants.GetTenantByFqdn;

public static class GetTenantByFqdnEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/by-fqdn/{fqdn}", async (string fqdn, IMediator mediator, CancellationToken ct) =>
            {
                var tenant = await mediator.Send(new GetTenantByFqdnQuery(fqdn), ct);
                return tenant is null ? Results.NotFound() : Results.Ok(tenant);
            })
            .WithName("GetTenantByFqdn")
            .WithSummary("Find a tenant by its fully-qualified domain name")
            .Produces<TenantResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }
}