namespace Sankore.Modules.Administration.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// The one place that decides where a tenant's email credential lives in the vault.
///
/// It sits in the PublicApi because two modules need to agree on it: M12 writes the secret when
/// an administrator saves the provider, and M08 reads it at send time. A convention duplicated
/// on both sides would drift, and the symptom would be "the password is saved but email still
/// fails" — the worst kind of bug to chase.
/// </summary>
public static class NotificationSecrets
{
    /// <summary>Scope under which every notification credential of a tenant is filed.</summary>
    public const string Scope = "notifications";

    /// <summary>
    /// One credential per provider, so switching from SMTP to Brevo and back does not destroy
    /// the other one's password.
    /// </summary>
    public static SecretKey CredentialKey(Guid tenantId, string providerType)
        => new(tenantId, Scope, Guid.Empty, $"{Normalize(providerType)}-credential");

    /// <summary>Lower-cased, so "Smtp", "SMTP" and "smtp" never produce two different secrets.</summary>
    private static string Normalize(string providerType)
        => string.IsNullOrWhiteSpace(providerType)
            ? "default"
            : providerType.Trim().ToLowerInvariant();
}
