namespace Sankore.Modules.Integration.Features.Commands.GetCommand;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetIntegrationCommandEndpoint
{
    internal static IEndpointRouteBuilder MapGetIntegrationCommand(this IEndpointRouteBuilder app)
    {
        app.MapGet("{commandId:guid}", Handle)
            .WithName("GetIntegrationCommand")
            .WithSummary("Read one integration command")
            .WithDescription(
                "Everything an operator needs to understand why a command is stuck: status, "
                + "attempts, next attempt, error family, code and detail, and the names of the "
                + "payload fields. NEVER the payload itself — it is encrypted under this "
                + "module's own key and no endpoint returns it. Requires Integration.Command.View.")
            .RequireAuthorization(Permissions.CanViewIntegrationCommand.Code)
            .Produces<IntegrationCommandDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid commandId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetIntegrationCommandQuery(commandId), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        // 404 for another tenant's command as much as for a non-existent one: the existence of a
        // command must not leak across tenants, and 403 would confirm it.
        return result.Error == IntegrationErrors.CommandNotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });
    }
}
