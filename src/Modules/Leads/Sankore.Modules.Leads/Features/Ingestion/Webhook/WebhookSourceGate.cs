namespace Sankore.Modules.Leads.Features.Ingestion.Webhook;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Who may push to a server-webhook source: the source is live, the caller's IP is allowed, the
/// signature verifies (<see cref="WebhookSigning"/>). Shared by the leads endpoint and the events
/// endpoint (webhooks lot 6) — the second one must not be the easier door to the same source.
/// A refusal is noted on the source (throttled), since it writes nothing else.
/// </summary>
internal sealed class WebhookSourceGate(
    LeadsDbContext db,
    ISecretsModule secrets,
    TimeProvider clock,
    ILogger<WebhookSourceGate> logger)
{
    /// <returns>The source when the push may proceed; otherwise the HTTP status to answer.</returns>
    public async Task<(LeadSourceConfig? Source, int Status)> AuthenticateAsync(
        string publicKey, string body, IReadOnlyDictionary<string, string> signatureHeaders, string remoteIp,
        CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(s => s.PublicKey == publicKey, ct);

        if (source is null || source.Status is not (LeadSourceStatus.Active or LeadSourceStatus.Testing))
            return (null, 404);

        var settings = source.Settings as ServerWebhookSettings;
        var now = clock.GetUtcNow();

        if (settings?.AllowedIps.Count > 0 && !settings.AllowedIps.Contains(remoteIp))
        {
            logger.LogWarning("Webhook IP rejected: {Ip} for source {SourceId}", remoteIp, source.Id);
            return await RejectAsync(source, 403, "IP_NOT_ALLOWED", now, ct);
        }

        // Required as soon as the source has a secret — or names an algorithm without one, which
        // is a misconfiguration that must fail closed rather than open.
        var (currentSecret, previousSecret) = await WebhookSigning.KeysAsync(secrets, source.TenantId, source.Id, now, ct);

        if (currentSecret is not null || settings?.SignatureAlgorithm is not null)
        {
            if (currentSecret is null)
            {
                logger.LogError("Webhook secret not found for source {SourceId}", source.Id);
                return await RejectAsync(source, 401, "SIGNATURE_SECRET_MISSING", now, ct);
            }

            signatureHeaders.TryGetValue(WebhookSigning.HeaderName(settings), out var signatureHeader);

            var sigError = WebhookSignatureVerifier.Verify(
                signatureHeader, body, WebhookSigning.Algorithm(settings), currentSecret, previousSecret, now);

            if (sigError is not null)
            {
                logger.LogWarning("Webhook signature failed: {Error} for source {SourceId}", sigError, source.Id);
                return await RejectAsync(source, 401, sigError, now, ct);
            }
        }

        return (source, 202);
    }

    private async Task<(LeadSourceConfig? Source, int Status)> RejectAsync(
        LeadSourceConfig source, int status, string reason, DateTimeOffset now, CancellationToken ct)
    {
        if (source.RecordRejection(reason, now))
            await db.SaveChangesAsync(ct);

        return (null, status);
    }
}
