using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.ActivateAgency;

public static class ActivateAgencyEndpoint
{
    public static IEndpointRouteBuilder MapActivateAgency(this IEndpointRouteBuilder app)
    {
        app.MapPost("{id:guid}/activate", Handle)
            .WithName("ActivateAgency")
            .WithSummary("Re-activate a soft-deleted agency")
            .WithDescription(
                "Reverses a soft-delete: sets IsActive=true and IsDeleted=false. " +
                "Requires permission: agency:activate.")
            .RequireAuthorization(Permissions.CanActivateAgency.Code)
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
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ActivateAgencyCommand(id), ct);

        if (!result.IsSuccess)
        {
            return result.Error!.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.NoContent();
    }
}
