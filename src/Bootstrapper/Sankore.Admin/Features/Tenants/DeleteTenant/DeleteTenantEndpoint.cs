using MediatR;

namespace Sankore.Admin.Features.Tenants.DeleteTenant;

public static class DeleteTenantEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapDelete("/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
            {
                var found = await mediator.Send(new DeleteTenantCommand(id), ct);
                return found ? Results.NoContent() : Results.NotFound();
            })
            .WithName("DeleteTenant")
            .WithSummary("Delete a tenant")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
    }
}