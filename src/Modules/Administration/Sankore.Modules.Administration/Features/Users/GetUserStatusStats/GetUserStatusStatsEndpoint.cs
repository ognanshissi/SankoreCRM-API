using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUserStatusStats;

public static class GetUserStatusStatsEndpoint
{
    public static IEndpointRouteBuilder MapGetUserStatusStats(this IEndpointRouteBuilder app)
    {
        app.MapGet("stats/status", Handle)
            .WithTags("Users")
            .WithName("GetUserStatusStats")
            .WithSummary("Get counts of users grouped by status")
            .RequireAuthorization(Permissions.CanReadUser.Code)
            .Produces<UserStatusStatsDto>()
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetUserStatusStatsQuery(), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
