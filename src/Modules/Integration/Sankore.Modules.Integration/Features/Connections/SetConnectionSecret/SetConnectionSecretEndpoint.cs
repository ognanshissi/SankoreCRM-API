namespace Sankore.Modules.Integration.Features.Connections.SetConnectionSecret;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The write half of a connection's credentials.
///
/// <para>
/// There is deliberately no route that returns a value. A screen needs to know "is this
/// configured, and is it the one I think it is" — the vault's masked hint answers both, and
/// anything more would turn an administration page into a way to read a credential back out.
/// That is what <c>GET connections/{connectionId}/secrets</c> is for.
/// </para>
/// </summary>
internal static class SetConnectionSecretEndpoint
{
    internal static IEndpointRouteBuilder MapSetConnectionSecret(this IEndpointRouteBuilder app)
    {
        app.MapPut("{connectionId:guid}/secrets/{name}", Handle)
            .WithName("SetIntegrationConnectionSecret")
            .WithSummary("Store one of a connection's credentials")
            .WithDescription(
                "Writes a credential into the secrets vault for this connection: "
                + "connection-credential (the OAuth secret, static token or API key an adapter "
                + "authenticates with), sftp-credential (the password or private key of a batch "
                + "connection) or webhook-secret (the HMAC secret inbound webhooks are verified "
                + "with). Keyed per CONNECTION, so storing an insurer's credential does not "
                + "destroy the core banking one. The value is write-only: it is never returned by "
                + "any endpoint, never logged, never put in an event, and recorded as \"***\" in "
                + "the audit trail. Audited. "
                + "Requires permission: Integration.Connection.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationConnection.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid connectionId,
        string name,
        SetConnectionSecretRequest req,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var result = await sender.Send(
            new SetConnectionSecretCommand(connectionId, name, req.Value, req.ExpiresAt), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound => Results.NotFound(new { error = result.Error }),
            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}

/// <summary>
/// The credential travels in the BODY, never in the route or the query string: a URL is written
/// to the access log of every proxy on the way, and a query string survives in a browser's
/// history.
/// </summary>
internal sealed record SetConnectionSecretRequest(string Value, DateTimeOffset? ExpiresAt);
