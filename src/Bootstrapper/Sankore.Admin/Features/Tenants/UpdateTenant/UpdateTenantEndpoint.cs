using MediatR;

namespace Sankore.Admin.Features.Tenants.UpdateTenant;

public static class UpdateTenantEndpoint
{
    public record Request(string Name, string Fqdn, bool IsActive, bool IsMaintenance);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPut("/{id:guid}", async (Guid id, Request req, IMediator mediator, CancellationToken ct) =>
            {
                try
                {
                    var found = await mediator.Send(
                        new UpdateTenantCommand(id, req.Name, req.Fqdn, req.IsActive, req.IsMaintenance), ct);
                    return found ? Results.NoContent() : Results.NotFound();
                }
                catch (InvalidOperationException)
                {
                    // The address belongs to another tenant: the same refusal as at creation.
                    return Results.Conflict(new { error = "TENANT_FQDN_TAKEN", fqdn = req.Fqdn });
                }
            })
            .WithName("UpdateTenant")
            .WithSummary("Update a tenant")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}
