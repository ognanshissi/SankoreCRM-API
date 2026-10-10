namespace Sankore.Modules.Integration.Features.Connections.SetConnectionSecret;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Writes the credential to the vault and nowhere else.
/// </summary>
internal sealed class SetConnectionSecretHandler(
    IntegrationDbContext db,
    ISecretsModule secrets,
    ICurrentUser currentUser,
    ILogger<SetConnectionSecretHandler> logger)
    : IRequestHandler<SetConnectionSecretCommand, Result>
{
    public async Task<Result> Handle(SetConnectionSecretCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // The connection must exist for THIS tenant. The global query filter does the scoping, so
        // another tenant's connection reads as absent and answers 404 — never 403. Without this
        // read a caller could seed vault entries under any id they cared to guess, and the
        // orphans would outlive every connection anyone could see.
        var exists = await db.Connections.AnyAsync(c => c.Id == cmd.ConnectionId, ct);

        if (!exists)
            return Result.Fail(IntegrationErrors.ConnectionNotFound);

        var key = ConnectionSecretNames.KeyFor(cmd.Name, currentUser.TenantId, cmd.ConnectionId);

        // Unreachable through the API — the validator refuses an unknown name — but a null key
        // would otherwise write nothing and report success.
        if (key is null)
            return Result.Fail(IntegrationErrors.SettingsInvalid);

        // Trimmed because a key pasted from a console or a mail usually carries a trailing
        // newline, and it would travel into an Authorization header as-is: a 401 whose cause is
        // invisible in both our logs and the far end's.
        await secrets.SetAsync(key, cmd.Value.Trim(), cmd.ExpiresAt, ct);

        // The slot, never any part of the value — not even a prefix: a log sink is chosen for
        // volume, not for secrecy.
        logger.LogInformation(
            "Credential {SecretName} stored for integration connection {ConnectionId} of tenant {TenantId}",
            cmd.Name, cmd.ConnectionId, currentUser.TenantId);

        return Result.Ok();
    }
}
