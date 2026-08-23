using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.CreateAgency;

public static class CreateAgencyEndpoint
{
    public static IEndpointRouteBuilder MapCreateAgency(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("CreateAgency")
            .WithSummary("Create a new agency")
            .WithDescription(
                "Creates an agency within the tenant. Non-HQ agencies must reference an existing parent. " +
                "Requires permission: agency:create.")
            .RequireAuthorization(Permissions.CanCreateAgency.Code)
            .Produces<CreateAgencyResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateAgencyRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateAgencyCommand(
            Name: req.Name,
            Description: req.Description ?? string.Empty,
            AgencyType: req.AgencyType,
            ParentAgencyId: req.ParentAgencyId,
            AddressStreet: req.AddressStreet,
            AddressCity: req.AddressCity,
            AddressState: req.AddressState,
            AddressCountry: req.AddressCountry,
            AddressZipCode: req.AddressZipCode,
            Latitude: req.Latitude,
            Longitude: req.Longitude), ct);

        return result.IsSuccess
            ? Results.Created($"/api/administration/agencies/{result.Value.AgencyId}", result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record CreateAgencyRequest(
    string Name,
    string? Description,
    AgencyType AgencyType,
    Guid? ParentAgencyId,
    string? AddressStreet,
    string? AddressCity,
    string? AddressState,
    string? AddressCountry,
    string? AddressZipCode,
    double? Latitude,
    double? Longitude);
