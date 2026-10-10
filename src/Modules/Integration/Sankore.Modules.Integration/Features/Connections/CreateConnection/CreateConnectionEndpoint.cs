namespace Sankore.Modules.Integration.Features.Connections.CreateConnection;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class CreateConnectionEndpoint
{
    internal static IEndpointRouteBuilder MapCreateConnection(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("CreateIntegrationConnection")
            .WithSummary("Declare an external system this tenant integrates with")
            .WithDescription(
                "Creates a core-banking or insurance connection for the CURRENT tenant. The "
                + "settings object is polymorphic: it carries a \"$kind\" naming the connection "
                + "kind and is validated against that kind's own rules, so a Temenos row cannot "
                + "hold Amplitude coordinates. A Relay connection is created WITHOUT its relay "
                + "agent: that link is server-set, by INT-27's enrolment flow, because an id "
                + "taken from a request body could name another tenant's on-premise agent. "
                + "NO credential travels here — the connection keeps "
                + "only a vault reference, and the values are written through "
                + "PUT connections/{connectionId}/secrets/{name}. The connection is created "
                + "INACTIVE: activation requires a passed health check (INT-03), which is a "
                + "second call. Audited. "
                + "Requires permission: Integration.Connection.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationConnection.Code)
            .Produces<CreateConnectionResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateConnectionRequest req,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var result = await sender.Send(
            new CreateConnectionCommand(
                Family: req.Family,
                Kind: req.Kind,
                Mode: req.Mode,
                Name: req.Name,
                Settings: req.Settings),
            ct);

        return result.IsSuccess
            ? Results.Created(
                $"api/v1/integration/connections/{result.Value}",
                new CreateConnectionResponse(result.Value))
            : Results.BadRequest(new { error = result.Error });
    }
}

/// <summary>
/// The wire shape. The tenant comes from the JWT and is never a field: a body that could name its
/// own tenant is a body that can write into another one. For the same reason there is no
/// <c>relayAgentId</c> — see <see cref="CreateConnectionCommand"/>.
/// </summary>
internal sealed record CreateConnectionRequest(
    IntegrationFamily Family,
    IntegrationKind Kind,
    IntegrationMode Mode,
    string Name,
    ConnectionSettings? Settings);

internal sealed record CreateConnectionResponse(Guid ConnectionId);
