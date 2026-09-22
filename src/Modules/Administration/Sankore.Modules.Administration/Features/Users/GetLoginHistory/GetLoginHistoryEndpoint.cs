namespace Sankore.Modules.Administration.Features.Users.GetLoginHistory;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

public static class GetLoginHistoryEndpoint
{
    public static IEndpointRouteBuilder MapGetLoginHistory(this IEndpointRouteBuilder app)
    {
        app.MapGet("users/me/login-history", GetMyHistory)
            .WithName("GetMyLoginHistory")
            .WithTags("Users")
            .WithSummary("Returns the connected user's login history")
            .RequireAuthorization()
            .Produces<IReadOnlyList<LoginHistoryDto>>()
            .WithOpenApi();

        app.MapGet("users/{userId:guid}/login-history", GetUserHistory)
            .WithName("GetUserLoginHistory")
            .WithTags("Users")
            .WithSummary("Returns a user's login history (admin)")
            .RequireAuthorization(Permissions.CanReadUser.Code)
            .Produces<IReadOnlyList<LoginHistoryDto>>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> GetMyHistory(
        ISender sender, ICurrentUser currentUser, CancellationToken ct,
        int page = 1, int pageSize = 20)
    {
        var result = await sender.Send(
            new GetLoginHistoryQuery(currentUser.Id, page, pageSize), ct);

        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetUserHistory(
        Guid userId, ISender sender, CancellationToken ct,
        int page = 1, int pageSize = 20)
    {
        var result = await sender.Send(
            new GetLoginHistoryQuery(userId, page, pageSize), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound();
    }
}
