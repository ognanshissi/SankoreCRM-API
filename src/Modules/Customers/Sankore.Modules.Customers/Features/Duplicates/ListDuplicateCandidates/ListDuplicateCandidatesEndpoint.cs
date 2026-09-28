namespace Sankore.Modules.Customers.Features.Duplicates.ListDuplicateCandidates;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ListDuplicateCandidatesEndpoint
{
    public static IEndpointRouteBuilder MapListDuplicateCandidates(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListDuplicateCandidates")
            .WithSummary("List duplicate client candidates")
            .WithDescription(
                "Paginated review queue of the client pairs flagged by the nightly detection, " +
                "highest match score first. Only pairs whose two clients are inside the caller's " +
                "agency perimeter are returned. Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<PagedResult<DuplicateCandidateDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        DuplicateCandidateStatus? status = null,
        int page = 1,
        int pageSize = 20)
    {
        var result = await sender.Send(new ListDuplicateCandidatesQuery(status, page, pageSize), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
