namespace Sankore.Modules.Leads.Features.Ingestion;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Unified lead ingestion pipeline (US-F13.37-BE-10).
/// LeadIngestion → FieldMapping → Dedup (phone blind index) → Lead.Capture.
/// Guarantees idempotence, deduplication, and consistent behavior across all modes.
/// </summary>
internal sealed class IngestInboundLeadHandler(
    LeadsDbContext db,
    IPhoneBlindIndexer phoneBlindIndexer,
    TimeProvider clock,
    Sankore.Modules.Administration.PublicApi.IAdministrationModule? administration = null,
    Microsoft.Extensions.Logging.ILogger<IngestInboundLeadHandler>? logger = null)
    : IRequestHandler<IngestInboundLeadCommand, Result<IngestInboundLeadResult>>
{
    public async Task<Result<IngestInboundLeadResult>> Handle(
        IngestInboundLeadCommand cmd, CancellationToken ct)
    {
        // ── 1. Idempotence check: (TenantId, SourceId, ExternalId) ──────
        if (cmd.ExternalId is not null)
        {
            var existing = await db.LeadIngestions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.TenantId == cmd.TenantId
                                          && i.SourceId == cmd.SourceId
                                          && i.ExternalId == cmd.ExternalId, ct);

            if (existing is not null)
            {
                var updated = await TryUpdateAsync(cmd, existing, ct);
                return Result.Ok(new IngestInboundLeadResult(
                    existing.Id,
                    existing.LeadId == Guid.Empty ? null : existing.LeadId,
                    existing.Status,
                    Updated: updated));
            }
        }

        // ── 2. Load source config ───────────────────────────────────────
        var source = await db.LeadSourceConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId && s.TenantId == cmd.TenantId, ct);

        if (source is null)
            return Result.Fail<IngestInboundLeadResult>("SOURCE_NOT_FOUND");

        if (source.Status is not (LeadSourceStatus.Active or LeadSourceStatus.Testing))
            return Result.Fail<IngestInboundLeadResult>("SOURCE_NOT_ACTIVE");

        // ── 3. Resolve field mapping and apply ──────────────────────────
        var fieldMapping = source.Settings.FieldMappingsOf();

        Dictionary<string, string?> mapped;
        if (fieldMapping is not null && fieldMapping.Count > 0)
        {
            var mappingResult = FieldMappingEngine.Apply(fieldMapping, cmd.RawPayloadJson);
            if (mappingResult.Errors.Count > 0)
            {
                var errorDetail = string.Join("; ", mappingResult.Errors.Select(e => $"{e.JsonPath}: {e.Message}"));
                var failedIngestion = LeadIngestion.CreateFailed(
                    cmd.TenantId, cmd.SourceId, $"MAPPING_ERROR:{errorDetail}",
                    clock, cmd.RunId, cmd.RawPayloadJson, cmd.ExternalId);
                db.LeadIngestions.Add(failedIngestion);
                await db.SaveChangesAsync(ct);
                return Result.Ok(new IngestInboundLeadResult(
                    failedIngestion.Id, null, failedIngestion.Status, errorDetail));
            }
            mapped = new Dictionary<string, string?>(mappingResult.MappedValues, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            // Internal mode or EmbeddedScript — payload IS the mapped fields (JSON)
            try
            {
                var obj = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(cmd.RawPayloadJson);
                mapped = new Dictionary<string, string?>(obj ?? [], StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                var failedIngestion = LeadIngestion.CreateFailed(
                    cmd.TenantId, cmd.SourceId, "INVALID_PAYLOAD",
                    clock, cmd.RunId, cmd.RawPayloadJson, cmd.ExternalId);
                db.LeadIngestions.Add(failedIngestion);
                await db.SaveChangesAsync(ct);
                return Result.Ok(new IngestInboundLeadResult(
                    failedIngestion.Id, null, failedIngestion.Status, "INVALID_PAYLOAD"));
            }
        }

        // ── 4. Required fields ──────────────────────────────────────────
        //
        // The name is DERIVED and no longer required outright (BNF-B-18): a v2 form collects a
        // first and a last name separately, and one rule — LeadNameResolver — now serves all
        // inbound paths.
        var missing = InboundLeadFactory.MissingRequiredFields(mapped, out var phone, out var fullName);
        if (missing.Count > 0)
        {
            var failedIngestion = LeadIngestion.CreateFailed(
                cmd.TenantId, cmd.SourceId,
                $"REQUIRED_FIELDS_MISSING:{string.Join(",", missing)}",
                clock, cmd.RunId, cmd.RawPayloadJson, cmd.ExternalId);
            db.LeadIngestions.Add(failedIngestion);
            await db.SaveChangesAsync(ct);
            return Result.Ok(new IngestInboundLeadResult(
                failedIngestion.Id, null, failedIngestion.Status,
                $"Required fields missing: {string.Join(", ", missing)}"));
        }

        // ── 5. Phone dedup via blind index within DedupWindowDays ───────
        var phoneIndex = phoneBlindIndexer.Compute(phone!);
        var duplicateLeadId = await InboundLeadFactory.FindDuplicateAsync(
            db, cmd.TenantId, phoneIndex, source, clock, ct);

        if (duplicateLeadId is { } dupId)
        {
            var dupIngestion = LeadIngestion.Create(
                cmd.TenantId, dupId, cmd.SourceId,
                clock, cmd.RunId, cmd.RawPayloadJson, cmd.ExternalId);
            dupIngestion.MarkDuplicate();
            db.LeadIngestions.Add(dupIngestion);

            // WA-BE-05: the same person filled the form again and, this time, ticked the WhatsApp
            // box — the opt-in belongs to the lead they already are.
            var existingLead = await db.Leads
                .IgnoreQueryFilters()
                .AsTracking()
                .FirstOrDefaultAsync(l => l.TenantId == cmd.TenantId && l.Id == dupId, ct);

            if (existingLead is not null && !existingLead.IsAnonymized)
            {
                await InboundWhatsAppConsent.RecordIfGivenAsync(
                    db, source, cmd.RawPayloadJson, existingLead, dupIngestion.Id, clock, ct);
            }

            await db.SaveChangesAsync(ct);
            return Result.Ok(new IngestInboundLeadResult(
                dupIngestion.Id, dupId, dupIngestion.Status));
        }

        // ── 6. Create lead ──────────────────────────────────────────────
        var lead = InboundLeadFactory.Build(
            cmd.TenantId, source, mapped, fullName!, phone!, phoneIndex, cmd.ExternalId, clock);

        // Mapping rules targeting `custom.<key>` (dynamic forms, lot 6).
        await CustomFields.InboundCustomFields.ApplyAsync(administration, logger, lead, mapped, source.Id, ct);

        db.Leads.Add(lead);
        await QualifyLead.CaptureScore.ApplyAsync(db, lead, ct);

        // ── 7. Create ingestion record ──────────────────────────────────
        var ingestion = LeadIngestion.Create(
            cmd.TenantId, lead.Id, cmd.SourceId,
            clock, cmd.RunId, cmd.RawPayloadJson, cmd.ExternalId);
        db.LeadIngestions.Add(ingestion);

        // WA-BE-05: the WhatsApp opt-in of the form, in the lead's own transaction — a lead saved
        // without the consent its prospect gave would have its « Contacter » button greyed out
        // for a consent that exists.
        await InboundWhatsAppConsent.RecordIfGivenAsync(
            db, source, cmd.RawPayloadJson, lead, ingestion.Id, clock, ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(new IngestInboundLeadResult(
            ingestion.Id, lead.Id, ingestion.Status));
    }

    /// <summary>
    /// The same external id again, on a server webhook that allows updates: the custom fields of
    /// the lead it created are merged from this push (webhooks lot 5). Custom fields ONLY — a
    /// provider must not move a lead's status, owner or what an agent typed, and a resend need not
    /// repeat the name and phone, so the mapping's missing-field errors are ignored here.
    /// </summary>
    private async Task<bool> TryUpdateAsync(IngestInboundLeadCommand cmd, LeadIngestion existing, CancellationToken ct)
    {
        if (existing.Status != LeadIngestionStatus.Accepted || existing.LeadId == Guid.Empty)
            return false;

        var source = await db.LeadSourceConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId && s.TenantId == cmd.TenantId, ct);

        if (source is not { Settings: ServerWebhookSettings { AllowUpdates: true } }
            || source.Status is not (LeadSourceStatus.Active or LeadSourceStatus.Testing))
            return false;

        var fieldMapping = source.Settings.FieldMappingsOf();
        if (fieldMapping is null || fieldMapping.Count == 0)
            return false;

        var mapped = new Dictionary<string, string?>(
            FieldMappingEngine.Apply(fieldMapping, cmd.RawPayloadJson).MappedValues, StringComparer.OrdinalIgnoreCase);

        var lead = await db.Leads
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(l => l.TenantId == cmd.TenantId && l.Id == existing.LeadId, ct);

        if (lead is null || lead.IsAnonymized || lead.Status == LeadStatus.Archived)
            return false;

        var before = lead.CustomFieldsJson;
        await CustomFields.InboundCustomFields.ApplyAsync(administration, logger, lead, mapped, source.Id, ct, mergeWithExisting: true);
        if (string.Equals(before, lead.CustomFieldsJson, StringComparison.Ordinal))
            return false;

        await db.SaveChangesAsync(ct);
        return true;
    }
}
