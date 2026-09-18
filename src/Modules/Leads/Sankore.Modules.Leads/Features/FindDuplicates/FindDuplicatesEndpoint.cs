namespace Sankore.Modules.Leads.Features.FindDuplicates;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class FindDuplicatesEndpoint
{
    public static IEndpointRouteBuilder MapFindDuplicates(this IEndpointRouteBuilder app)
    {
        app.MapGet("duplicates", Handle)
            .WithName("FindLeadDuplicates")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<IReadOnlyList<DuplicateMatchResult>>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        string? phoneNumber        = null,
        string? email              = null,
        string? nationalId         = null,
        string? customerReference  = null,
        string? fullName           = null,
        DateOnly? dateOfBirth      = null,
        double? latitude           = null,
        double? longitude          = null,
        double? minConfidence      = null,
        Guid? sourceLeadId         = null)
    {
        var result = await sender.Send(new FindDuplicatesQuery(
            PhoneNumber:       phoneNumber,
            Email:             email,
            NationalId:        nationalId,
            CustomerReference: customerReference,
            FullName:          fullName,
            DateOfBirth:       dateOfBirth,
            Latitude:          latitude,
            Longitude:         longitude,
            MinConfidence:     minConfidence,
            SourceLeadId:      sourceLeadId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(title: "Duplicate search failed", detail: result.Error, statusCode: 422);
    }
}
