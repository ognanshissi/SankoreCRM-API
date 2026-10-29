namespace Sankore.Modules.Leads.Features.Ingestion.Webhook;

using MediatR;
using Newtonsoft.Json.Linq;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Webhook ingestion handler (US-F13.37-BE-17).
/// Resolves source, verifies signature + IP, parses batch, delegates to IngestInboundLeadCommand.
/// </summary>
internal sealed class IngestWebhookHandler(
    ISender sender,
    WebhookSourceGate gate)
    : IRequestHandler<IngestWebhookCommand, Result<IngestWebhookResult>>
{
    private const int MaxBatchSize = 100;

    public async Task<Result<IngestWebhookResult>> Handle(
        IngestWebhookCommand cmd, CancellationToken ct)
    {
        // ── 1-3. Source, IP allow-list, signature ───────────────────────
        var (source, status) = await gate.AuthenticateAsync(cmd.PublicKey, cmd.Body, cmd.SignatureHeaders, cmd.RemoteIp, ct);
        if (source is null)
            return Result.Ok(new IngestWebhookResult(status, []));

        var settings = source.Settings as ServerWebhookSettings;

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
            results.Add(new WebhookItemResult(v.IngestionId, v.LeadId, v.Updated ? "Updated" : v.Status.ToString(), v.Error));
        }

        return Result.Ok(new IngestWebhookResult(202, results));
    }
}
