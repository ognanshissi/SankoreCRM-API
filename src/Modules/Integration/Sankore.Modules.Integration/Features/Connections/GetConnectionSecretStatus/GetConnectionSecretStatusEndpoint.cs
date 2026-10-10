namespace Sankore.Modules.Integration.Features.Connections.GetConnectionSecretStatus;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The read half of a connection's credentials: which slots are filled, and a masked hint of
/// each. Never a value — there is no route in this module that returns one.
/// </summary>
internal static class GetConnectionSecretStatusEndpoint
{
    internal static IEndpointRouteBuilder MapGetConnectionSecretStatus(this IEndpointRouteBuilder app)
    {
        app.MapGet("{connectionId:guid}/secrets", Handle)
            .WithName("GetIntegrationConnectionSecretStatus")
            .WithSummary("Which of a connection's credentials are stored, and their masked hints")
            .WithDescription(
                "One row per credential slot (connection-credential, sftp-credential, "
                + "webhook-secret) saying whether it is configured, the vault's own masked hint "
                + "and the expiry the operator declared. The hint is enough to tell two "
                + "credentials apart while rotating one, never enough to use either — the values "
                + "themselves are not retrievable by any endpoint. A connection of another tenant "
                + "answers 404, never 403. "
                + "Requires permission: Integration.Connection.Manage.")
            // Manage and not View: knowing which credentials an institution holds, and when they
            // expire, is operational information for whoever may change them.
            .RequireAuthorization(Permissions.CanManageIntegrationConnection.Code)
            .Produces<IReadOnlyList<ConnectionSecretStatusDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    /// <summary>
    /// Reads the hints DIRECTLY rather than through MediatR: there is no decision to take, no
    /// transaction to open and nothing to audit about asking whether a setting exists. The
    /// precedent is M02's <c>BiometryTokenEndpoints.Status</c>.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid connectionId,
        IntegrationDbContext db,
        ISecretsModule secrets,
        ITenantContext tenant,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(tenant);

        var tenantId = tenant.CurrentTenantId;

        // Scoped by the DbContext's global query filter: another tenant's connection reads as
        // absent and answers 404, never 403 — otherwise this endpoint would confirm that an id
        // names a real connection on the platform, and say which credentials it holds.
        var exists = await db.Connections.AnyAsync(c => c.Id == connectionId, ct);

        if (!exists)
            return Results.NotFound(new { error = IntegrationErrors.ConnectionNotFound });

        // Every declared slot is reported, filled or not. Which ones an adapter actually needs is
        // the adapter's business; a screen that only saw the filled ones could not offer to fill
        // the others.
        var statuses = new List<ConnectionSecretStatusDto>(ConnectionSecretNames.All.Length);

        foreach (var name in ConnectionSecretNames.All)
        {
            var key = ConnectionSecretNames.KeyFor(name, tenantId, connectionId);
            if (key is null) continue;

            var hint = await secrets.GetHintAsync(key, ct);

            statuses.Add(new ConnectionSecretStatusDto(
                Name: name,
                IsConfigured: hint is not null,
                MaskedValue: hint?.MaskedValue,
                ExpiresAt: hint?.ExpiresAt));
        }

        return Results.Ok(statuses);
    }
}
