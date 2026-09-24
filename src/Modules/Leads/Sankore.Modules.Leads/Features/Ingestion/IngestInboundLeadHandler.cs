namespace Sankore.Modules.Leads.Features.Ingestion;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

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

        var isTest = source.Status == LeadSourceStatus.Testing;

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

        // ── 4. Extract required fields ──────────────────────────────────
        mapped.TryGetValue("phoneNumber", out var phone);
        mapped.TryGetValue("fullName", out var fullName);

        if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(fullName))
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(phone)) missing.Add("phoneNumber");
            if (string.IsNullOrWhiteSpace(fullName)) missing.Add("fullName");

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
        var phoneIndex = phoneBlindIndexer.Compute(phone);
        var dedupCutoff = clock.GetUtcNow().AddDays(-source.DedupWindowDays);

        var duplicateLead = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == cmd.TenantId
                        && l.PhoneBlindIndex == phoneIndex
                        && l.Status != LeadStatus.Lost
                        && l.Status != LeadStatus.Archived
                        && l.Status != LeadStatus.Disqualified
                        && l.CapturedAt >= dedupCutoff)
            .Select(l => new { l.Id })
            .FirstOrDefaultAsync(ct);

        if (duplicateLead is not null)
        {
            var dupIngestion = LeadIngestion.Create(
                cmd.TenantId, duplicateLead.Id, cmd.SourceId,
                clock, cmd.RunId, cmd.RawPayloadJson, cmd.ExternalId);
            dupIngestion.MarkDuplicate();
            db.LeadIngestions.Add(dupIngestion);
            await db.SaveChangesAsync(ct);
            return Result.Ok(new IngestInboundLeadResult(
                dupIngestion.Id, duplicateLead.Id, dupIngestion.Status));
        }

        // ── 6. Create lead ──────────────────────────────────────────────
        mapped.TryGetValue("firstName", out var firstName);
        mapped.TryGetValue("lastName", out var lastName);
        mapped.TryGetValue("email", out var email);
        mapped.TryGetValue("interestedProduct", out var product);
        mapped.TryGetValue("preferredLanguage", out var lang);
        mapped.TryGetValue("nationalId", out var nationalId);
        mapped.TryGetValue("customerReference", out var custRef);
        mapped.TryGetValue("comment", out var comment);
        mapped.TryGetValue("campaign", out var campaign);
        mapped.TryGetValue("externalReference", out var extRef);
        mapped.TryGetValue("externalId", out var extId);
        mapped.TryGetValue("utmSource", out var utmSource);
        mapped.TryGetValue("utmMedium", out var utmMedium);
        mapped.TryGetValue("utmCampaign", out var utmCampaign);
        mapped.TryGetValue("utmContent", out var utmContent);
        mapped.TryGetValue("utmTerm", out var utmTerm);
        mapped.TryGetValue("landingPage", out var landingPage);
        mapped.TryGetValue("referrer", out var referrer);

        double.TryParse(mapped.GetValueOrDefault("latitude"), out var lat);
        double.TryParse(mapped.GetValueOrDefault("longitude"), out var lng);

        var lead = Lead.Capture(
            tenantId:          cmd.TenantId,
            fullName:          fullName!,
            phoneNumber:       phone!,
            source:            LeadSource.Web, // Source enum legacy — actual source tracked via LeadIngestion
            interestedProduct: product ?? "Unknown",
            preferredLanguage: lang ?? "FR",
            location:          new GeoPoint(lat, lng),
            preferredAgencyId: null,
            clock:             clock,
            firstName:         firstName,
            lastName:          lastName,
            email:             email,
            nationalId:        nationalId,
            customerReference: custRef,
            comment:           comment,
            campaign:          campaign,
            externalReference: extRef,
            externalId:        extId ?? cmd.ExternalId,
            phoneBlindIndex:   phoneIndex,
            utmSource:         utmSource,
            utmMedium:         utmMedium,
            utmCampaign:       utmCampaign,
            utmContent:        utmContent,
            utmTerm:           utmTerm,
            landingPage:       landingPage,
            referrer:          referrer,
            acquisitionCost:   source.CostPerLead,
            isTest:            isTest);

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
