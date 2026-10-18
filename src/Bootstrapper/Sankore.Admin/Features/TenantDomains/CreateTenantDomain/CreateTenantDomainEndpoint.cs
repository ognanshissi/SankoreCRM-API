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
                var result = await mediator.Send(
                    new CreateTenantDomainCommand(
                        req.TenantId, req.Fqdn ?? string.Empty, req.IsPrimary, req.ValidFrom, req.ValidTo), ct);

                return result.Error switch
                {
                    CreateTenantDomainError.None =>
                        Results.Created($"/api/v1/tenant-domains/{result.Id}", new CreateTenantDomainResponse(result.Id!.Value)),
                    CreateTenantDomainError.TenantNotFound => Results.Problem(result.Message, statusCode: StatusCodes.Status404NotFound),
                    CreateTenantDomainError.Conflict => Results.Problem(result.Message, statusCode: StatusCodes.Status409Conflict),
                    _ => Results.Problem(result.Message, statusCode: StatusCodes.Status400BadRequest),
                };
            })
            .WithName("CreateTenantDomain")
            .WithSummary("Assign a new FQDN to a tenant")
            .Produces<CreateTenantDomainResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}
