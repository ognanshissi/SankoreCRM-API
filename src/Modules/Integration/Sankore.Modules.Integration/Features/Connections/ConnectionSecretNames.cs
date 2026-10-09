namespace Sankore.Modules.Integration.Features.Connections;

using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// The credentials a connection may hold, by the name the route carries.
///
/// <para>
/// The names are <see cref="IntegrationSecrets"/>' own constants rather than friendlier aliases:
/// one spelling for the route, the vault key and the operator's vocabulary means a rotation
/// script and a screen cannot disagree about which slot they are writing.
/// </para>
/// </summary>
internal static class ConnectionSecretNames
{
    public static readonly string[] All =
    [
        IntegrationSecrets.CredentialName,
        IntegrationSecrets.SftpCredentialName,
        IntegrationSecrets.WebhookSecretName,
    ];

    public static bool IsKnown(string? name)
        => name is not null && All.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// The vault key for one name, or <c>null</c> when the name is not one of ours. Null rather
    /// than a thrown exception: the caller turns it into a validation failure naming the allowed
    /// values, which is what an operator can act on.
    /// </summary>
    public static SecretKey? KeyFor(string? name, Guid tenantId, Guid connectionId) => name switch
    {
        IntegrationSecrets.CredentialName => IntegrationSecrets.CredentialKey(tenantId, connectionId),
        IntegrationSecrets.SftpCredentialName => IntegrationSecrets.SftpCredentialKey(tenantId, connectionId),
        IntegrationSecrets.WebhookSecretName => IntegrationSecrets.WebhookSecretKey(tenantId, connectionId),
        _ => null,
    };
}
