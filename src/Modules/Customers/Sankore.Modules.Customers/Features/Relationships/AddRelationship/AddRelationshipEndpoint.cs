using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Relationships.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.Relationships.AddRelationship;

public static class AddRelationshipEndpoint
{
    public static IEndpointRouteBuilder MapAddRelationship(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("AddClientRelationship")
            .WithSummary("Link a client to another client or to an external person")
            .WithDescription(
                "Provide either relatedClientId or externalFullName, never both. " +
                "Relating a client to itself is refused with SELF_RELATIONSHIP_FORBIDDEN. " +
                "A Spouse link between two clients also creates the mirror relationship on the " +
                "other client, and the two are tied by reciprocalRelationshipId. " +
                "A Guarantor link publishes GuarantorLinkedEvent. Child and Dependent links " +
                "recompute the client's dependentsCount. " +
                "Requires permission: customers:update.")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
            .Produces<AddRelationshipResult>(StatusCodes.Status201Created)
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
        AddRelationshipRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new AddRelationshipCommand(
            ClientId: clientId,
            Type: req.Type,
            RelatedClientId: req.RelatedClientId,
            ExternalFullName: req.ExternalFullName,
            ExternalPhoneNumber: req.ExternalPhoneNumber,
            ExternalDateOfBirth: req.ExternalDateOfBirth,
            ExternalDocumentNumber: req.ExternalDocumentNumber), ct);

        if (result.IsFailure)
            return RelationshipHttp.ToProblem(result.Error);

        return Results.Created(
            $"/api/v1/clients/{clientId}/relationships/{result.Value.RelationshipId}",
            result.Value);
    }
}

public sealed record AddRelationshipRequest(
    RelationshipType Type,
    Guid? RelatedClientId,
    string? ExternalFullName,
    string? ExternalPhoneNumber,
    DateOnly? ExternalDateOfBirth,
    string? ExternalDocumentNumber);
