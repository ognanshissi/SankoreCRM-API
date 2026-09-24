namespace Sankore.Modules.Leads.Features.Ingestion.Webhook;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Webhook ingestion handler (US-F13.37-BE-17).
/// Resolves source, verifies signature + IP, parses batch, delegates to IngestInboundLeadCommand.
/// </summary>
internal sealed class IngestWebhookHandler(
    LeadsDbContext db,
    ISender sender,
    ISecretsModule secrets,
    ILogger<IngestWebhookHandler> logger)
    : IRequestHandler<IngestWebhookCommand, Result<IngestWebhookResult>>
{
    private const int MaxBatchSize = 100;

    public async Task<Result<IngestWebhookResult>> Handle(
        IngestWebhookCommand cmd, CancellationToken ct)
    {
        // ── 1. Resolve source ───────────────────────────────────────────
        var source = await db.LeadSourceConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.PublicKey == cmd.PublicKey, ct);

        if (source is null || source.Status == LeadSourceStatus.Archived)
            return Result.Ok(new IngestWebhookResult(404, []));

        if (source.Status is not (LeadSourceStatus.Active or LeadSourceStatus.Testing))
            return Result.Ok(new IngestWebhookResult(404, []));

        var settings = source.Settings as ServerWebhookSettings;

        // ── 2. IP allowlist ─────────────────────────────────────────────
        if (settings?.AllowedIpAddresses.Count > 0
            && !settings.AllowedIpAddresses.Contains(cmd.RemoteIp))
        {
            logger.LogWarning("Webhook IP rejected: {Ip} for source {SourceId}", cmd.RemoteIp, source.Id);
            return Result.Ok(new IngestWebhookResult(403, []));
        }

        // ── 3. Signature verification ───────────────────────────────────
        if (settings?.SignatureAlgorithm is not null)
        {
            var currentKey = new SecretKey(source.TenantId, "LeadSource", source.Id, "hmac-signing");
            var currentSecret = await secrets.GetValueAsync(currentKey, ct);

            if (currentSecret is null)
            {
                logger.LogError("Webhook secret not found for source {SourceId}", source.Id);
                return Result.Ok(new IngestWebhookResult(401, []));
            }

            var oldKey = new SecretKey(source.TenantId, "LeadSource", source.Id, "hmac-signing-old");
            var previousSecret = await secrets.GetValueAsync(oldKey, ct);

            var sigError = WebhookSignatureVerifier.Verify(
                cmd.SignatureHeader, cmd.Body, settings.SignatureAlgorithm,
                currentSecret, previousSecret, DateTimeOffset.UtcNow);

            if (sigError is not null)
            {
                logger.LogWarning("Webhook signature failed: {Error} for source {SourceId}", sigError, source.Id);
                return Result.Ok(new IngestWebhookResult(401, []));
            }
        }

        // ── 4. Parse payload — single or batch ──────────────────────────
        JToken parsed;
        try
        {
            parsed = JToken.Parse(cmd.Body);
        }
        catch
        {
            return Result.Ok(new IngestWebhookResult(400,
                [new WebhookItemResult(null, null, "Failed", "INVALID_JSON")]));
        }

        var items = parsed switch
        {
            JArray arr => arr.ToList(),
            JObject obj => [obj],
            _ => []
        };

        if (items.Count == 0)
            return Result.Ok(new IngestWebhookResult(400,
                [new WebhookItemResult(null, null, "Failed", "EMPTY_PAYLOAD")]));

        if (items.Count > MaxBatchSize)
            return Result.Ok(new IngestWebhookResult(400,
                [new WebhookItemResult(null, null, "Failed", $"BATCH_TOO_LARGE: max {MaxBatchSize}")]));

        // ── 5. Process each item ────────────────────────────────────────
        var results = new List<WebhookItemResult>(items.Count);

        foreach (var item in items)
        {
            string? externalId = null;
            if (settings?.ExternalIdPath is not null)
            {
                var extToken = item.SelectToken(settings.ExternalIdPath);
                externalId = extToken?.ToString();

                if (string.IsNullOrEmpty(externalId))
                {
                    results.Add(new WebhookItemResult(null, null, "Rejected", "ExternalIdMissing"));
                    continue;
                }
            }

            var rawJson = item.ToString(Newtonsoft.Json.Formatting.None);

            var ingestResult = await sender.Send(new IngestInboundLeadCommand(
                TenantId:       source.TenantId,
                SourceId:       source.Id,
                RawPayloadJson: rawJson,
                ExternalId:     externalId), ct);

            if (ingestResult.IsFailure)
            {
                results.Add(new WebhookItemResult(null, null, "Failed", ingestResult.Error));
                continue;
            }

            var v = ingestResult.Value;
            results.Add(new WebhookItemResult(v.IngestionId, v.LeadId, v.Status.ToString(), v.Error));
        }

        return Result.Ok(new IngestWebhookResult(202, results));
    }
}
