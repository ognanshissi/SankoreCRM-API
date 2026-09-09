using MediatR;

namespace Sankore.Admin.Features.Tenants.CreateTenant;

public static class CreateTenantEndpoint
{
    public record Request(string Name, string RootUserEmail, string Fqdn, DateTimeOffset? TrialExpiresAt);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/", async (Request req, IMediator mediator, CancellationToken ct) =>
            {
                var id = await mediator.Send(
                    new CreateTenantCommand(req.Name, req.RootUserEmail, req.Fqdn, req.TrialExpiresAt), ct);
                return Results.Created($"/api/v1/tenants/{id}", new { id });
            })
            .WithName("CreateTenant")
            .WithSummary("Create a new tenant")
            .Produces<object>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest);
    }
}
