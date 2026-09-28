namespace Sankore.Modules.Customers.Features.Duplicates.Merge.ListClientMerges;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Duplicates.Merge.GetClientMerge;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ListClientMergesEndpoint
{
    public static IEndpointRouteBuilder MapListClientMerges(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListClientMerges")
            .WithSummary("List client merge requests")
            .WithDescription(
                "Paginated merge requests, most recent first, optionally filtered by status. Only " +
                "requests whose two clients are inside the caller's agency perimeter are returned. " +
                "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<PagedResult<ClientMergeRequestDto>>(StatusCodes.Status200OK)
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
        MergeRequestStatus? status = null,
        int page = 1,
        int pageSize = 20)
    {
        var result = await sender.Send(new ListClientMergesQuery(status, page, pageSize), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
