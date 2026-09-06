using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.ListUsers;

public static class ListUsersEndpoint
{
    public static IEndpointRouteBuilder MapListUsers(this IEndpointRouteBuilder app)
    {
        app.MapGet("", Handle)
            .WithTags("Users")
            .WithName("ListUsers")
            .WithSummary("List users with optional filters")
            .RequireAuthorization(Permissions.CanReadUser.Code)
            .Produces<ListUsersResult>()
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        UserStatus? status = null,
        Guid? agencyId = null,
        string? search = null,
        int page = 1,
        int pageSize = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var result = await sender.Send(
            new ListUsersQuery(status, agencyId, search, page, pageSize), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
