namespace Sankore.Modules.Kyc.Features.Settings.SetBiometryToken;

using MediatR;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Shared.Kernel;

/// <summary>
/// Writes the token to the vault and nowhere else. There is no table, no column and no event: the
/// value only ever has one home, which is what keeps it out of a GET, a log and an audit payload.
/// </summary>
internal sealed class SetBiometryTokenHandler(
    ISecretsModule secrets,
    ITenantContext tenant,
    ILogger<SetBiometryTokenHandler> logger)
    : IRequestHandler<SetBiometryTokenCommand, Result>
{
    public async Task<Result> Handle(SetBiometryTokenCommand cmd, CancellationToken ct)
    {
        var tenantId = tenant.CurrentTenantId;

        // Trimmed because a token pasted from a console or a mail usually carries a trailing
        // newline, and it would travel into the Authorization header as-is — a 401 whose cause is
        // invisible in both our logs and the service's.
        await secrets.SetAsync(
            BiometrySecrets.TokenKey(tenantId),
            cmd.Token.Trim(),
            expiresAt: null,
            ct);

        // The tenant id only. Logging any part of the value — even a prefix — would put it in a
        // sink chosen for volume rather than for secrecy.
        logger.LogInformation(
            "Biometry service token stored for tenant {TenantId}", tenantId);

        return Result.Ok();
    }
}
