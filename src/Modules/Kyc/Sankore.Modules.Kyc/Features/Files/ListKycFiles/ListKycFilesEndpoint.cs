namespace Sankore.Modules.Kyc.Features.Files.ListKycFiles;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListKycFilesEndpoint
{
    internal static IEndpointRouteBuilder MapListKycFiles(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListKycFiles")
            .WithSummary("List KYC files in the caller's agency perimeter")
            .WithDescription(
                "Server-side paging, filterable by status, agency, vigilance level and a period on "
                + "last activity. Always bounded to the caller's agency perimeter, and files whose "
                + "agency could not be resolved are visible only to an unrestricted caller. Rows "
                + "whose next approval rung the caller's roles can sign come first, and "
                + "awaitingMeCount counts those across the whole perimeter rather than the page. "
                + "Carries no customer name: resolve it from the clients API. Requires kyc:read.")
            .RequireAuthorization(Permissions.CanReadKycFile.Code)
            .Produces<KycFileListPage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        [FromQuery] string? status,
        [FromQuery] Guid? agencyId,
        [FromQuery] string? vigilanceLevel,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        ISender sender,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await sender.Send(
            new ListKycFilesQuery(status, agencyId, vigilanceLevel, from, to, page, pageSize), ct);

        // 400, not 404: an unparsable status or vigilance level is a malformed request, and the
        // message names the accepted values. Nothing here can be "not found" — an empty perimeter
        // is an empty page, which is a success.
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
