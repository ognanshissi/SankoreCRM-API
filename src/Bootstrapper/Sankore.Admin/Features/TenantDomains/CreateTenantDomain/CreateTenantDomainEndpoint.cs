using MediatR;

namespace Sankore.Admin.Features.TenantDomains.CreateTenantDomain;

public static class CreateTenantDomainEndpoint
{
    public record Request(
        Guid TenantId,
        string Fqdn,
        bool IsPrimary,
        DateTimeOffset? ValidFrom,
        DateTimeOffset? ValidTo);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/", async (Request req, IMediator mediator, CancellationToken ct) =>
            {
                var id = await mediator.Send(
                    new CreateTenantDomainCommand(
                        req.TenantId, req.Fqdn, req.IsPrimary, req.ValidFrom, req.ValidTo), ct);
                return Results.Created($"/api/v1/tenant-domains/{id}", new { id });
            })
            .WithName("CreateTenantDomain")
            .WithSummary("Assign a new FQDN to a tenant")
            .Produces<CreateTenantDomainResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi();
    }
}