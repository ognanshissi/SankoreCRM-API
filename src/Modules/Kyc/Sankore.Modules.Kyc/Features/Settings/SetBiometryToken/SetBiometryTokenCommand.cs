namespace Sankore.Modules.Kyc.Features.Settings.SetBiometryToken;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Stores the tenant's bearer token for the external biometric service.
///
/// <para>
/// This is the writer <c>BiometrySecrets</c> always assumed ("only M02 writes it, from the KYC
/// settings screen") and which did not exist: nothing in the solution called
/// <c>ISecretsModule.SetAsync</c> for that key, so <c>HttpBiometryClient</c> read a slot nobody
/// could fill and every verification answered BIOMETRY_NOT_CONFIGURED. Setting it in
/// configuration did not work either — <c>BiometryOptions</c> has no token property, so a
/// <c>Kyc:Biometry:Token</c> entry bound to nothing and was silently ignored.
/// </para>
///
/// <para>
/// The token is per tenant even though the Flask deployment is shared: that is how the service
/// attributes a call, and how one tenant's access is revoked without a redeploy.
/// </para>
/// </summary>
/// <param name="Token">
/// The bearer value. <see cref="SensitiveDataAttribute"/> is load-bearing, not decoration:
/// <c>ICommand</c> makes AuditBehavior serialize this record into <c>audit.entries</c>, and
/// without it the credential would be written in clear to the one table built to be read later.
/// The attribute replaces it with "***" there — the same thing M12 does for its provider
/// credential. The vault is the only place the value lands.
/// </param>
internal sealed record SetBiometryTokenCommand([property: SensitiveData] string Token)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "KycBiometryToken";

    /// <summary>One entry per tenant, so there is no row id to name.</summary>
    public string? ResourceId => null;
}

/// <summary>What a screen can show without ever receiving the value back.</summary>
/// <param name="IsConfigured">Whether a token is stored for this tenant.</param>
/// <param name="MaskedValue">
/// The vault's own masked hint, e.g. "biom…AqM=". Null when nothing is stored. Enough to tell two
/// tokens apart when rotating, never enough to use one.
/// </param>
internal sealed record BiometryTokenStatusDto(
    bool IsConfigured,
    string? MaskedValue,
    DateTimeOffset? ExpiresAt);
