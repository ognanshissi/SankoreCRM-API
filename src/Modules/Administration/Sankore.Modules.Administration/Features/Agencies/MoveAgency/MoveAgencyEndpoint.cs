using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.MoveAgency;

public static class MoveAgencyEndpoint
{
    public static IEndpointRouteBuilder MapMoveAgency(this IEndpointRouteBuilder app)
    {
        app.MapPost("{id:guid}/move", Handle)
            .WithName("MoveAgency")
            .WithSummary("Change the parent of an agency")
            .WithDescription(
                "Reparents an agency. " +
                "Pass null for newParentAgencyId to promote a HeadQuarter agency to root level. " +
                "Circular references are rejected. " +
                "Requires permission: agency:move.")
            .RequireAuthorization(Permissions.CanMoveAgency.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id,
        MoveAgencyRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new MoveAgencyCommand(id, req.NewParentAgencyId), ct);

        if (!result.IsSuccess)
        {
            return result.Error!.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.NoContent();
    }
}

public sealed record MoveAgencyRequest(Guid? NewParentAgencyId);
