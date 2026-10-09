namespace Sankore.Modules.Integration.Features.Connections.UpdateConnection;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class UpdateConnectionEndpoint
{
    internal static IEndpointRouteBuilder MapUpdateConnection(this IEndpointRouteBuilder app)
    {
        app.MapPut("{connectionId:guid}", Handle)
            .WithName("UpdateIntegrationConnection")
            .WithSummary("Replace the coordinates of one connection")
            .WithDescription(
                "Rewrites the name, the mode and the WHOLE settings object. The relay agent is "
                + "NOT writable here — it is server-set by INT-27's enrolment flow, because an id "
                + "taken from a request body could point this connection at another tenant's "
                + "on-premise agent; an existing link is preserved. The "
                + "family and the kind cannot change: a different system is a different "
                + "connection, and switching the kind would leave this connection's references, "
                + "commands and call logs pointing at a system that never knew them — settings "
                + "belonging to another kind answer 400 INTEGRATION_SETTINGS_INVALID. Send "
                + "expectedVersion (the row version a GET returned) or expectedUpdatedAt; a "
                + "concurrent change answers 409 INTEGRATION_CONCURRENCY_CONFLICT. No credential "
                + "travels here. Audited. "
                + "Requires permission: Integration.Connection.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationConnection.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid connectionId,
        UpdateConnectionRequest req,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The id comes from the route and never from the body: a mismatch between the two would
        // let a caller update a connection other than the one the URL — and every log of it —
        // names.
        var result = await sender.Send(
            new UpdateConnectionCommand(
                ConnectionId: connectionId,
                Name: req.Name,
                Mode: req.Mode,
                Settings: req.Settings,
                ExpectedVersion: req.ExpectedVersion,
                ExpectedUpdatedAt: req.ExpectedUpdatedAt),
            ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound => Results.NotFound(new { error = result.Error }),
            IntegrationErrors.ConcurrencyConflict => Results.Conflict(new { error = result.Error }),
            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}

/// <summary>
/// The wire shape. No <c>relayAgentId</c>: see <see cref="UpdateConnectionCommand"/> — a relay
/// agent id a client can set is a cross-tenant leak, and the stored one is carried through.
/// </summary>
internal sealed record UpdateConnectionRequest(
    string Name,
    IntegrationMode Mode,
    ConnectionSettings? Settings,
    uint? ExpectedVersion,
    DateTimeOffset? ExpectedUpdatedAt);
