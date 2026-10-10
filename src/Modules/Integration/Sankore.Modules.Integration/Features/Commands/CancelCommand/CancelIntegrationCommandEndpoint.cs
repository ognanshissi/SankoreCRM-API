namespace Sankore.Modules.Integration.Features.Commands.CancelCommand;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class CancelIntegrationCommandEndpoint
{
    internal static IEndpointRouteBuilder MapCancelIntegrationCommand(this IEndpointRouteBuilder app)
    {
        app.MapPost("{commandId:guid}/cancel", Handle)
            .WithName("CancelIntegrationCommand")
            .WithSummary("Abandon a queued or rejected integration command")
            .WithDescription(
                "Final. Possible only from Pending and Rejected: a command already in flight or "
                + "already confirmed by the external system cannot be cancelled, and answers 409. "
                + "Audited with its author. Requires Integration.Command.Replay.")
            .RequireAuthorization(Permissions.CanReplayIntegrationCommand.Code)
            .Produces<CancelIntegrationCommandResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid commandId,
        CancelIntegrationCommandRequest? req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CancelIntegrationCommandCommand(commandId, req?.Reason), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        return result.Error switch
        {
            IntegrationErrors.CommandNotFound => Results.NotFound(new { error = result.Error }),
            IntegrationErrors.CommandNotCancellable => Results.Conflict(new { error = result.Error }),
            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}

/// <param name="Reason">
/// Operator-facing motive. It lands in the audit row with the author, not on the command: see the
/// handler for why the diagnosis column is left alone.
/// </param>
internal sealed record CancelIntegrationCommandRequest(string? Reason = null);
