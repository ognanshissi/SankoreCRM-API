using MediatR;

namespace Sankore.Admin.Features.TenantDomains.DeactivateTenantDomain;

public static class DeactivateTenantDomainEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapDelete("/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new DeactivateTenantDomainCommand(id), ct);
                if (result.Refusal is { } refusal)
                    return Results.Problem(refusal, statusCode: StatusCodes.Status409Conflict);
                return result.Success ? Results.NoContent() : Results.NotFound();
            })
            .WithName("DeactivateTenantDomain")
            .WithSummary("Deactivate a tenant domain entry (never the primary one)")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}
