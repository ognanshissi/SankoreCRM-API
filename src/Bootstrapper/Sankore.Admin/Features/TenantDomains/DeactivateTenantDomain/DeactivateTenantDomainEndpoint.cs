using MediatR;

namespace Sankore.Admin.Features.TenantDomains.DeactivateTenantDomain;

public static class DeactivateTenantDomainEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapDelete("/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
            {
                var found = await mediator.Send(new DeactivateTenantDomainCommand(id), ct);
                return found ? Results.NoContent() : Results.NotFound();
            })
            .WithName("DeactivateTenantDomain")
            .WithSummary("Deactivate a tenant domain entry")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }
}