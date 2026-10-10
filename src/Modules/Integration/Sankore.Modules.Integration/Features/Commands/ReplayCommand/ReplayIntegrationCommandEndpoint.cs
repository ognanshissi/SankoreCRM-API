namespace Sankore.Modules.Integration.Features.Commands.ReplayCommand;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ReplayIntegrationCommandEndpoint
{
    internal static IEndpointRouteBuilder MapReplayIntegrationCommand(this IEndpointRouteBuilder app)
    {
        // No request body at all, by design: a replay re-sends what was recorded. See
        // ReplayIntegrationCommandCommand for why a caller-supplied payload is refused here.
        app.MapPost("{commandId:guid}/replay", Handle)
            .WithName("ReplayIntegrationCommand")
            .WithSummary("Put a rejected integration command back in the queue")
            .WithDescription(
                "Only a Rejected command can be replayed, and the attempt counter is reset so a "
                + "fixed configuration gets a full budget. The command is re-sent as recorded — "
                + "the payload cannot be supplied or edited by the caller; a wrong value is fixed "
                + "in the CRM record, which the dispatcher re-reads when it sends. Audited with "
                + "its author. Requires Integration.Command.Replay.")
            .RequireAuthorization(Permissions.CanReplayIntegrationCommand.Code)
            .Produces<ReplayIntegrationCommandResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid commandId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ReplayIntegrationCommandCommand(commandId), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        return result.Error switch
        {
            // 404 and never 403: a command of another tenant is absent, and "forbidden" would
            // confirm it exists.
            IntegrationErrors.CommandNotFound => Results.NotFound(new { error = result.Error }),

            // 409 rather than 400: the request was well formed, it arrived late. Somebody else
            // replayed or cancelled the command between the screen's read and this call, and the
            // front recovers by reloading rather than by asking the user to fix a field.
            IntegrationErrors.CommandNotReplayable => Results.Conflict(new { error = result.Error }),

            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}
