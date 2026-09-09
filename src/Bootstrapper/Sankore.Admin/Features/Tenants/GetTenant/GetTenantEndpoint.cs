using MediatR;

namespace Sankore.Admin.Features.Tenants.GetTenant;

public static class GetTenantEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
            {
                var tenant = await mediator.Send(new GetTenantQuery(id), ct);
                return tenant is null ? Results.NotFound() : Results.Ok(tenant);
            })
            .WithName("GetTenant")
            .WithSummary("Get a tenant by ID")
            .Produces<TenantResponse>()
            .Produces(StatusCodes.Status404NotFound);
    }
}
