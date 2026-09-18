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
            .Produces<IReadOnlyList<LeadDuplicateDto>>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        string? phoneNumber = null,
        string? email = null)
    {
        var result = await sender.Send(new FindDuplicatesQuery(phoneNumber, email), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(title: "Duplicate search failed", detail: result.Error, statusCode: 422);
    }
}
