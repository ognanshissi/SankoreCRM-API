namespace Sankore.Modules.Kyc.Infrastructure.Biometry;

using Sankore.Shared.Kernel;

/// <summary>
/// The one place that decides where a tenant's biometric service token lives in the vault,
/// in the same spirit as <c>NotificationSecrets</c>.
///
/// Unlike that one it is <c>internal</c>: only M02 writes it (from the KYC settings screen) and
/// only M02 reads it (at verification time), so the convention has no reason to leave the module.
/// Should another module ever need it, move this to the PublicApi rather than copying the shape —
/// a duplicated convention drifts, and the symptom is "the token is saved but verification still
/// says not configured".
/// </summary>
internal static class BiometrySecrets
{
    /// <summary>Scope under which every KYC credential of a tenant is filed.</summary>
    public const string Scope = "kyc";

    /// <summary>Name of the bearer token entry. One per tenant, hence <see cref="Guid.Empty"/>.</summary>
    public const string TokenName = "biometry-service-token";

    /// <summary>
    /// The token is per tenant even though the Flask deployment is shared: that is how the service
    /// attributes a call, and how a single tenant's access can be revoked without a redeploy.
    /// </summary>
    public static SecretKey TokenKey(Guid tenantId) => new(tenantId, Scope, Guid.Empty, TokenName);
}
