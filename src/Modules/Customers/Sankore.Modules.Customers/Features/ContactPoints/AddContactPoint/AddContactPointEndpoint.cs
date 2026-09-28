using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.ContactPoints.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.ContactPoints.AddContactPoint;

public static class AddContactPointEndpoint
{
    public static IEndpointRouteBuilder MapAddContactPoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("AddClientContactPoint")
            .WithSummary("Add a phone, email or postal address to a client")
            .WithDescription(
                "The value is encrypted at rest and indexed blindly, and is dated with validFrom. " +
                "Posting a value that is already on file for the same type returns the existing " +
                "contact point (alreadyExisted=true) instead of creating a duplicate. " +
                "The first active contact point of a type automatically becomes the primary one. " +
                "Requires permission: customers:update.")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
            .Produces<AddContactPointResult>(StatusCodes.Status201Created)
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
        AddContactPointRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new AddContactPointCommand(
            ClientId: clientId,
            Type: req.Type,
            Value: req.Value,
            Label: req.Label,
            MakePrimary: req.MakePrimary), ct);

        if (result.IsFailure)
            return ContactPointHttp.ToProblem(result.Error);

        return Results.Created(
            $"/api/v1/clients/{clientId}/contact-points/{result.Value.ContactPointId}",
            result.Value);
    }
}

public sealed record AddContactPointRequest(
    ContactPointType Type,
    string Value,
    string? Label,
    bool MakePrimary);
