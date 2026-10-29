namespace Sankore.Modules.Leads.Features.Ingestion.Webhook;

using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// The one answer to « must this push be signed, with what, and where is the signature » for a
/// server-webhook source (webhooks lot 5) — read by the endpoint, the activation and the provider
/// documentation, so the three cannot disagree.
///
/// <para>
/// <b>A secret in the vault means a signature is required.</b> Generating it (« Générer le secret
/// de signature ») is the administrator's act of asking for signed pushes. It used to be ignored:
/// the endpoint verified only when <c>SignatureAlgorithm</c> was set, which no screen ever set, so
/// a source showing a secret accepted unsigned pushes from anyone holding its URL.
/// </para>
/// </summary>
internal static class WebhookSigning
{
    public const string Scope = "LeadSource";
    public const string CurrentName = "hmac-signing";
    public const string PreviousName = "hmac-signing-old";
    public const string DefaultHeader = "X-Sankore-Signature";
    public const string DefaultAlgorithm = "sha256";

    public static string Algorithm(ServerWebhookSettings? settings)
        => string.IsNullOrWhiteSpace(settings?.SignatureAlgorithm) ? DefaultAlgorithm : settings!.SignatureAlgorithm!;

    public static string HeaderName(ServerWebhookSettings? settings)
        => string.IsNullOrWhiteSpace(settings?.SignatureHeaderName) ? DefaultHeader : settings!.SignatureHeaderName!.Trim();

    public static async Task<bool> HasSecretAsync(ISecretsModule vault, Guid tenantId, Guid sourceId, CancellationToken ct)
        => await vault.GetHintAsync(new SecretKey(tenantId, Scope, sourceId, CurrentName), ct) is not null;

    /// <summary>
    /// The current secret, and the previous one while its rotation window is open. The vault keeps
    /// an expired value and returns it: the expiry is checked here — before, a rotated-out secret
    /// went on authenticating pushes forever.
    /// </summary>
    public static async Task<(string? Current, string? Previous)> KeysAsync(
        ISecretsModule vault, Guid tenantId, Guid sourceId, DateTimeOffset now, CancellationToken ct)
    {
        var current = await vault.GetValueAsync(new SecretKey(tenantId, Scope, sourceId, CurrentName), ct);

        var previousKey = new SecretKey(tenantId, Scope, sourceId, PreviousName);
        var hint = await vault.GetHintAsync(previousKey, ct);
        var previous = hint is not null && (hint.ExpiresAt is null || hint.ExpiresAt > now)
            ? await vault.GetValueAsync(previousKey, ct)
            : null;

        // A blank value signs nothing and must not read as « a secret is set ».
        return (string.IsNullOrEmpty(current) ? null : current, string.IsNullOrEmpty(previous) ? null : previous);
    }
}
