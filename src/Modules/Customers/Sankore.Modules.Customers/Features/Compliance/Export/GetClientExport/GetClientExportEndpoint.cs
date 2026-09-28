namespace Sankore.Modules.Customers.Features.Compliance.Export.GetClientExport;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class GetClientExportEndpoint
{
    public static IEndpointRouteBuilder MapGetClientExport(this IEndpointRouteBuilder app)
    {
        app.MapGet("exports/{exportId:guid}", Handle)
            .WithName("GetClientExport")
            .WithSummary("Read the status of a client export")
            .WithDescription(
                "Returns the status, row count, expiry and — once the file is ready and the link "
                + "is still live — the download URL carrying the one-shot token. An export is "
                + "private to its requester: any other caller gets 404 EXPORT_NOT_FOUND, because "
                + "the response would otherwise hand them a bearer token over personal data. "
                + "Requires permission: customers:export.")
            .RequireAuthorization(Permissions.CanExportCustomers.Code)
            .Produces<ClientExportStatusDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid exportId,
        HttpContext http,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetClientExportQuery(exportId), ct);

        if (result.IsFailure)
            return ComplianceHttp.ToProblem(result.Error);

        // The body carries the download token: it must not sit in a shared or browser cache.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";

        return Results.Ok(result.Value);
    }
}
