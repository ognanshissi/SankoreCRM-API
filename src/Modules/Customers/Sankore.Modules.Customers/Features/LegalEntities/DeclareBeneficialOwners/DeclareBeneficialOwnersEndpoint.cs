namespace Sankore.Modules.Customers.Features.LegalEntities.DeclareBeneficialOwners;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class DeclareBeneficialOwnersEndpoint
{
    public static IEndpointRouteBuilder MapDeclareBeneficialOwners(this IEndpointRouteBuilder app)
    {
        app.MapPost("{clientId:guid}/beneficial-owners", Handle)
            .WithName("DeclareBeneficialOwners")
            .WithSummary("Declare the complete beneficial-owner structure of a legal client")
            .WithDescription(
                "Set-based declaration: active owners absent from the payload are closed " +
                "(ValidTo) and never deleted, owners not yet recorded are created. " +
                "Rejects OWNERSHIP_EXCEEDS_100 when the declared stakes exceed 100 %, and " +
                "MANAGER_BENEFICIAL_OWNER_REQUIRED when nobody reaches the tenant threshold " +
                "(beneficial-owner-threshold, default 25) and no manager is named. " +
                "An individual client answers CLIENT_NOT_LEGAL_ENTITY; an archived or merged " +
                "client answers CLIENT_READ_ONLY. Identity documents of external owners are " +
                "stored encrypted. Requires permission: customers:update_sensitive.")
            .RequireAuthorization(Permissions.CanUpdateCustomerSensitive.Code)
            .Produces<DeclareBeneficialOwnersResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid clientId,
        DeclareBeneficialOwnersRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new DeclareBeneficialOwnersCommand(
            ClientId: clientId,
            Owners: req.Owners ?? [],
            Reason: req.Reason), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            CustomerErrors.ClientNotFound => Results.Problem(
                result.Error, statusCode: StatusCodes.Status404NotFound),
            CustomerErrors.ClientReadOnly => Results.Problem(
                result.Error, statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest),
        };
    }
}

public sealed record DeclareBeneficialOwnersRequest(
    IReadOnlyList<BeneficialOwnerInput>? Owners,
    string Reason);
