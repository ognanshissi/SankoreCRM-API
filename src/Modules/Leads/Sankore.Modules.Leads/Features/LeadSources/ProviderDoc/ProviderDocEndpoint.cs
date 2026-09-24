namespace Sankore.Modules.Leads.Features.LeadSources.ProviderDoc;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class ProviderDocEndpoint
{
    public static IEndpointRouteBuilder MapProviderDocEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("lead-sources/{id:guid}/provider-doc", Handle)
            .WithName("GetProviderDoc")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanReadLeadSources.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetProviderDocQuery(id), ct);

        if (!result.IsSuccess)
        {
            return result.Error is "SOURCE_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(detail: result.Error, statusCode: 422);
        }

        return Results.File(
            result.Value.PdfBytes,
            contentType: "application/pdf",
            fileDownloadName: result.Value.FileName);
    }
}
