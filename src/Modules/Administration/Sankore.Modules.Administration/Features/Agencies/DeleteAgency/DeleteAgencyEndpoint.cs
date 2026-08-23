using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.DeleteAgency;

public static class DeleteAgencyEndpoint
{
    public static IEndpointRouteBuilder MapDeleteAgency(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{id:guid}", Handle)
            .WithName("DeleteAgency")
            .WithSummary("Soft-delete an agency")
            .WithDescription(
                "Marks the agency as deleted and inactive. " +
                "Fails if the agency still has users assigned to it. " +
                "Requires permission: agency:delete.")
            .RequireAuthorization(Permissions.CanDeleteAgency.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DeleteAgencyCommand(id), ct);

        if (!result.IsSuccess)
        {
            return result.Error!.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.NoContent();
    }
}
