namespace Sankore.Modules.Customers.Features.Groups.ListGroups;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ListGroupsEndpoint
{
    public static IEndpointRouteBuilder MapListGroups(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListClientGroups")
            .WithSummary("List client groups")
            .WithDescription(
                "Returns a paginated list of groups, ordered by name. " +
                "Filters: type, status, agencyId, search (substring of the name). " +
                "Results are always restricted to the caller's agency perimeter, so an agencyId " +
                "outside it returns an empty page rather than 403. " +
                "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<PagedResult<GroupListItemDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        GroupType? type,
        GroupStatus? status,
        Guid? agencyId,
        string? search,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var result = await sender.Send(new ListGroupsQuery(
            Type: type,
            Status: status,
            AgencyId: agencyId,
            Search: search,
            Page: page ?? 1,
            PageSize: pageSize ?? 20), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(
                title: "Group listing failed", detail: result.Error,
                statusCode: StatusCodes.Status400BadRequest);
    }
}
