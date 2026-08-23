using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.UpdateAgency;

public static class UpdateAgencyEndpoint
{
    public static IEndpointRouteBuilder MapUpdateAgency(this IEndpointRouteBuilder app)
    {
        app.MapPut("{id:guid}", Handle)
            .WithName("UpdateAgency")
            .WithSummary("Update an agency's details")
            .RequireAuthorization(Permissions.CanUpdateAgency.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id,
        UpdateAgencyRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateAgencyCommand(
            AgencyId: id,
            Name: req.Name,
            Description: req.Description ?? string.Empty,
            AgencyType: req.AgencyType,
            AddressStreet: req.AddressStreet,
            AddressCity: req.AddressCity,
            AddressState: req.AddressState,
            AddressCountry: req.AddressCountry,
            AddressZipCode: req.AddressZipCode,
            Latitude: req.Latitude,
            Longitude: req.Longitude), ct);

        if (!result.IsSuccess)
        {
            return result.Error!.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.NoContent();
    }
}

public sealed record UpdateAgencyRequest(
    string Name,
    string? Description,
    AgencyType AgencyType,
    string? AddressStreet,
    string? AddressCity,
    string? AddressState,
    string? AddressCountry,
    string? AddressZipCode,
    double? Latitude,
    double? Longitude);
