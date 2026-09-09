using MediatR;

namespace Sankore.Admin.Features.Tenants.UpdateTenant;

public static class UpdateTenantEndpoint
{
    public record Request(string Name, string Fqdn, bool IsActive, bool IsMaintenance);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPut("/{id:guid}", async (Guid id, Request req, IMediator mediator, CancellationToken ct) =>
            {
                var found = await mediator.Send(
                    new UpdateTenantCommand(id, req.Name, req.Fqdn, req.IsActive, req.IsMaintenance), ct);
                return found ? Results.NoContent() : Results.NotFound();
            })
            .WithName("UpdateTenant")
            .WithSummary("Update a tenant")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }
}
