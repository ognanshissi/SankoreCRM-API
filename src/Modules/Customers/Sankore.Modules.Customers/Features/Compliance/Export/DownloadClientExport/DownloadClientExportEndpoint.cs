namespace Sankore.Modules.Customers.Features.Compliance.Export.DownloadClientExport;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class DownloadClientExportEndpoint
{
    public static IEndpointRouteBuilder MapDownloadClientExport(this IEndpointRouteBuilder app)
    {
        app.MapGet("exports/{exportId:guid}/download", Handle)
            .WithName("DownloadClientExport")
            .WithSummary("Download a generated client export")
            .WithDescription(
                "Serves the CSV against the token issued with the export. The token is compared in "
                + "constant time; a wrong token answers 404 EXPORT_NOT_FOUND (never a distinct "
                + "error, which would confirm the export id), and a link past its TTL answers 409 "
                + "EXPORT_LINK_EXPIRED. The response is Cache-Control: no-store — the body is "
                + "personal data. Requires permission: customers:export.")
            .RequireAuthorization(Permissions.CanExportCustomers.Code)
            .Produces<byte[]>(StatusCodes.Status200OK, contentType: "text/csv")
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
        Guid exportId,
        HttpContext http,
        ISender sender,
        CancellationToken ct,
        string? token = null)
    {
        var result = await sender.Send(new DownloadClientExportQuery(exportId, token ?? string.Empty), ct);

        // Set before returning either way: an error body must not be cached either, and the
        // headers have to be on the response before the result starts writing.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";

        if (result.IsFailure)
            return ComplianceHttp.ToProblem(result.Error);

        var file = result.Value;

        return Results.File(file.Content, file.ContentType, file.FileName);
    }
}
