namespace Sankore.Modules.Customers.Features.Clients.UpdateClientSensitive;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class UpdateClientSensitiveEndpoint
{
    public static IEndpointRouteBuilder MapUpdateClientSensitive(this IEndpointRouteBuilder app)
    {
        app.MapPatch("{clientId:guid}/sensitive", Handle)
            .WithName("UpdateClientSensitive")
            .WithSummary("Update the regulated identity fields of a client")
            .WithDescription(
                "Partial update of the legal names, the identity document and the postal address. "
                + "A motive of 10 to 500 characters is mandatory (REASON_REQUIRED otherwise) and is "
                + "kept in the audit trail. "
                + "Returns the list of field NAMES that actually changed — never a value, so the "
                + "response can be logged safely. "
                + "Submitting an identity document number that already belongs to another client "
                + "answers 409 DUPLICATE_IDENTITY_DOCUMENT. A stale expectedVersion answers 409 "
                + "CONCURRENCY_CONFLICT, an archived or merged client 409 CLIENT_READ_ONLY, and an "
                + "unknown or out-of-perimeter client 404 CLIENT_NOT_FOUND. "
                + "A new address closes the previous one (ValidTo) instead of overwriting it. "
                + "Requires permission: customers:update_sensitive.")
            .RequireAuthorization(Permissions.CanUpdateCustomerSensitive.Code)
            .Produces<IReadOnlyList<string>>(StatusCodes.Status200OK)
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
        UpdateClientSensitiveRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateClientSensitiveCommand(
            ClientId: clientId,
            ExpectedVersion: req.ExpectedVersion,
            Reason: req.Reason,
            FirstName: req.FirstName,
            LastName: req.LastName,
            MaidenName: req.MaidenName,
            IdentityDocumentType: req.IdentityDocumentType,
            IdentityDocumentNumber: req.IdentityDocumentNumber,
            IdentityDocumentIssuedOn: req.IdentityDocumentIssuedOn,
            IdentityDocumentExpiresOn: req.IdentityDocumentExpiresOn,
            Address: req.Address), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            CustomerErrors.ClientNotFound =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound),
            CustomerErrors.ConcurrencyConflict
                or CustomerErrors.ClientReadOnly
                or CustomerErrors.DuplicateIdentityDocument =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),
            CustomerErrors.AgencyOutOfScope =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest)
        };
    }
}

public sealed record UpdateClientSensitiveRequest(
    uint ExpectedVersion,
    string Reason,
    string? FirstName,
    string? LastName,
    string? MaidenName,
    IdentityDocumentType? IdentityDocumentType,
    string? IdentityDocumentNumber,
    DateOnly? IdentityDocumentIssuedOn,
    DateOnly? IdentityDocumentExpiresOn,
    PostalAddressInput? Address);
