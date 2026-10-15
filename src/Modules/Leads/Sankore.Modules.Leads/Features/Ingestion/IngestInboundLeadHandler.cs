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
    TimeProvider clock)
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
                return Result.Ok(new IngestInboundLeadResult(
                    existing.Id,
                    existing.LeadId == Guid.Empty ? null : existing.LeadId,
                    existing.Status));
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
            await db.SaveChangesAsync(ct);
            return Result.Ok(new IngestInboundLeadResult(
                dupIngestion.Id, dupId, dupIngestion.Status));
        }

        // ── 6. Create lead ──────────────────────────────────────────────
        var lead = InboundLeadFactory.Build(
            cmd.TenantId, source, mapped, fullName!, phone!, phoneIndex, cmd.ExternalId, clock);

        db.Leads.Add(lead);

        // ── 7. Create ingestion record ──────────────────────────────────
        var ingestion = LeadIngestion.Create(
            cmd.TenantId, lead.Id, cmd.SourceId,
            clock, cmd.RunId, cmd.RawPayloadJson, cmd.ExternalId);
        db.LeadIngestions.Add(ingestion);

        await db.SaveChangesAsync(ct);

        return Result.Ok(new IngestInboundLeadResult(
            ingestion.Id, lead.Id, ingestion.Status));
    }
}
