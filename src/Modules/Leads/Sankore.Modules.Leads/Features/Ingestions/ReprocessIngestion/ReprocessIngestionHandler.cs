namespace Sankore.Modules.Leads.Features.Ingestions.ReprocessIngestion;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Ingestion;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Re-runs the tail of the inbound pipeline — required fields, phone dedup, lead creation — on
/// a stored payload, with the operator's links laid over the source's own mapping.
///
/// <para>Not a variant of replay. <c>ReplayIngestionJob</c> deserializes the payload straight
/// into a <c>CaptureLeadCommand</c> and never touches the ingestion row, so a payload whose keys
/// are not lead field names fails again and the row keeps saying "failed" even when a lead was
/// eventually created. Here the row is the thing being completed, and it ends up pointing at
/// the lead.</para>
/// </summary>
internal sealed class ReprocessIngestionHandler(
    LeadsDbContext db,
    IPhoneBlindIndexer phoneBlindIndexer,
    TimeProvider clock,
    Sankore.Modules.Administration.PublicApi.IAdministrationModule? administration = null,
    Microsoft.Extensions.Logging.ILogger<ReprocessIngestionHandler>? logger = null)
    : IRequestHandler<ReprocessIngestionCommand, Result<ReprocessIngestionResult>>
{
    public async Task<Result<ReprocessIngestionResult>> Handle(
        ReprocessIngestionCommand cmd, CancellationToken ct)
    {
        var ingestion = await db.LeadIngestions
            .AsTracking()
            .FirstOrDefaultAsync(i => i.Id == cmd.IngestionId, ct);

        if (ingestion is null)
            return Result.Fail<ReprocessIngestionResult>("INGESTION_NOT_FOUND");

        if (ingestion.Status is not (LeadIngestionStatus.Rejected or LeadIngestionStatus.Failed))
            return Result.Fail<ReprocessIngestionResult>("ONLY_REJECTED_OR_FAILED_CAN_BE_REPROCESSED");

        // The row is the only trustworthy record of where the payload came from: the source is
        // read from it, never from the request or the payload.
        var source = await db.LeadSourceConfigs
            .FirstOrDefaultAsync(s => s.Id == ingestion.SourceId, ct);

        if (source is null)
            return Result.Fail<ReprocessIngestionResult>("SOURCE_NOT_FOUND");

        if (source.Status == LeadSourceStatus.Archived)
            return Result.Fail<ReprocessIngestionResult>("SOURCE_ARCHIVED");

        if (!TryFlatten(ingestion.RawPayloadJson, out var payload))
            return Result.Fail<ReprocessIngestionResult>("INVALID_PAYLOAD");

        // Start from what the pipeline would have mapped, so fields the source already maps
        // correctly are kept; the operator only supplies what was missing. Mapping errors are not
        // fatal here — the operator's links are precisely how such a payload gets completed.
        var rules = source.Settings.FieldMappingsOf();
        var mapped = rules is { Count: > 0 }
            ? new Dictionary<string, string?>(
                FieldMappingEngine.Apply(rules, ingestion.RawPayloadJson!).MappedValues,
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(payload, StringComparer.OrdinalIgnoreCase);

        foreach (var (target, key) in cmd.FieldLinks)
        {
            if (!payload.TryGetValue(key, out var value))
                return Result.Fail<ReprocessIngestionResult>($"PAYLOAD_FIELD_NOT_FOUND:{key}");
            mapped[target] = value?.Trim();
        }

        var missing = InboundLeadFactory.MissingRequiredFields(mapped, out var phone, out var fullName);
        if (missing.Count > 0)
            return Result.Fail<ReprocessIngestionResult>(
                $"REQUIRED_FIELDS_MISSING:{string.Join(",", missing)}");

        var phoneIndex = phoneBlindIndexer.Compute(phone!);
        var duplicateLeadId = await InboundLeadFactory.FindDuplicateAsync(
            db, ingestion.TenantId, phoneIndex, source, clock, ct);

        if (duplicateLeadId is { } dupId)
        {
            ingestion.MarkReprocessedAsDuplicateOf(dupId);
            await db.SaveChangesAsync(ct);
            return Result.Ok(new ReprocessIngestionResult(dupId, ingestion.Status));
        }

        var lead = InboundLeadFactory.Build(
            ingestion.TenantId, source, mapped, fullName!, phone!, phoneIndex,
            ingestion.ExternalId, clock);
        await Features.CustomFields.InboundCustomFields.ApplyAsync(administration, logger, lead, mapped, source.Id, ct);
        db.Leads.Add(lead);
        ingestion.MarkReprocessed(lead.Id);

        await db.SaveChangesAsync(ct);

        return Result.Ok(new ReprocessIngestionResult(lead.Id, ingestion.Status));
    }

    /// <summary>
    /// Top-level keys of the payload as text — the shape a web form posts and the one the
    /// operator links from. A nested value is kept as its JSON text rather than dropped.
    /// </summary>
    private static bool TryFlatten(string? raw, out Dictionary<string, string?> values)
    {
        values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => property.Value.GetRawText(),
                };
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
