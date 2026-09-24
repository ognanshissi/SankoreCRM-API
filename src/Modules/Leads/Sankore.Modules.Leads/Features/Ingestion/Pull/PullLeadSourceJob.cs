namespace Sankore.Modules.Leads.Features.Ingestion.Pull;

using System.Text.Json;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Published when a source auto-transitions to Error after 3 consecutive failures.
/// Consumed by notification handlers to alert tenant administrators.
/// </summary>
public sealed record LeadSourceFailedEvent(
    Guid SourceId, Guid TenantId, string Code, string Error) : IntegrationEventBase;

/// <summary>
/// Hangfire job — pulls leads from a single ScheduledPull source.
/// Creates a LeadSourceRun as distributed lock (Running status = lock held).
/// Persists cursor after each page for crash-safe resumption.
/// After 3 consecutive failures → source transitions to Error + event published.
/// </summary>
public sealed class PullLeadSourceJob(IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(Guid sourceId, Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db     = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();
        var puller = scope.ServiceProvider.GetRequiredService<GenericRestPuller>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var bus    = scope.ServiceProvider.GetRequiredService<IBus>();
        var clock  = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<PullLeadSourceJob>>();

        var now = clock.GetUtcNow();

        // ── Load source ─────────────────────────────────────────────────
        var source = await db.LeadSourceConfigs
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sourceId && s.TenantId == tenantId);

        if (source is null)
        {
            logger.LogWarning("Pull: source {SourceId} not found", sourceId);
            return;
        }

        if (source.Status != LeadSourceStatus.Active)
        {
            logger.LogDebug("Pull: source {SourceId} is not Active, skipping", sourceId);
            return;
        }

        var settings = source.Settings as ScheduledPullSettings;
        if (settings is null)
        {
            logger.LogWarning("Pull: source {SourceId} has no ScheduledPullSettings", sourceId);
            return;
        }

        // ── Create run (distributed lock) ───────────────────────────────
        var run = LeadSourceRun.Start(tenantId, sourceId, clock);
        db.LeadSourceRuns.Add(run);
        await db.SaveChangesAsync();

        int fetched = 0, ingested = 0, rejected = 0, duplicates = 0;

        try
        {
            // ── Fetch all pages ─────────────────────────────────────────
            var since = source.LastPullAt;
            var result = await puller.FetchAsync(source, settings, since, CancellationToken.None);

            if (!result.Success)
            {
                throw new Exception(result.Error ?? "Pull failed");
            }

            fetched = result.Items.Count;

            // ── Process each item ───────────────────────────────────────
            foreach (var item in result.Items)
            {
                try
                {
                    // Extract external ID for idempotency
                    string? externalId = null;
                    if (settings.ExternalIdPath is not null)
                    {
                        var el = item;
                        foreach (var seg in settings.ExternalIdPath.TrimStart('$', '.').Split('.'))
                        {
                            if (el.TryGetProperty(seg, out el)) continue;
                            el = default;
                            break;
                        }
                        externalId = el.ValueKind != JsonValueKind.Undefined ? el.ToString() : null;
                    }

                    // Check idempotency
                    if (externalId is not null)
                    {
                        var exists = await db.LeadIngestions
                            .IgnoreQueryFilters()
                            .AnyAsync(i => i.TenantId == tenantId
                                        && i.SourceId == sourceId
                                        && i.ExternalId == externalId);
                        if (exists)
                        {
                            duplicates++;
                            continue;
                        }
                    }

                    // Map fields to CaptureLeadCommand
                    var cmd = MapToCommand(item, settings, tenantId, source);

                    if (cmd is null)
                    {
                        // Record failed ingestion
                        db.LeadIngestions.Add(LeadIngestion.CreateFailed(
                            tenantId, sourceId, "FIELD_MAPPING_FAILED", clock,
                            run.Id, item.GetRawText(), externalId));
                        rejected++;
                        continue;
                    }

                    var captureResult = await sender.Send(cmd, CancellationToken.None);

                    if (captureResult.IsSuccess && captureResult.Value?.LeadId is not null)
                    {
                        db.LeadIngestions.Add(LeadIngestion.Create(
                            tenantId, captureResult.Value.LeadId.Value, sourceId, clock,
                            run.Id, item.GetRawText(), externalId));
                        ingested++;
                    }
                    else
                    {
                        db.LeadIngestions.Add(LeadIngestion.CreateFailed(
                            tenantId, sourceId, captureResult.Error ?? "CAPTURE_FAILED", clock,
                            run.Id, item.GetRawText(), externalId));
                        rejected++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Pull: item processing failed for source {SourceId}", sourceId);
                    rejected++;
                }
            }

            // ── Persist cursor and mark success ─────────────────────────
            source.AdvanceCursor(null, now); // Cursor managed by puller internally
            source.RecordPullSuccess(now);
            run.Complete(fetched, ingested, rejected, duplicates, clock);

            await db.SaveChangesAsync();

            logger.LogInformation(
                "Pull completed for source {SourceId}: {Fetched} fetched, {Ingested} ingested, {Rejected} rejected, {Duplicates} duplicates",
                sourceId, fetched, ingested, rejected, duplicates);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pull failed for source {SourceId}", sourceId);

            run.Fail(ex.Message, clock);
            var transitionedToError = source.RecordPullFailure(ex.Message);

            await db.SaveChangesAsync();

            // 3 consecutive failures → publish LeadSourceFailed event
            if (transitionedToError)
            {
                await bus.Publish(new LeadSourceFailedEvent(
                    source.Id, tenantId, source.Code, ex.Message));

                logger.LogWarning(
                    "Source {SourceId} ({Code}) transitioned to Error after {Failures} consecutive failures",
                    sourceId, source.Code, source.ConsecutiveFailures);
            }
        }
    }

    private static CaptureLeadCommand? MapToCommand(
        JsonElement item, ScheduledPullSettings settings,
        Guid tenantId, LeadSourceConfig source)
    {
        var mapping = settings.FieldMapping;
        if (mapping is null or { Count: 0 })
            return null;

        string? GetField(string leadField)
        {
            var externalField = mapping.FirstOrDefault(m =>
                m.Value.Equals(leadField, StringComparison.OrdinalIgnoreCase)).Key;
            if (externalField is null) return null;

            return item.TryGetProperty(externalField, out var val)
                ? val.ValueKind == JsonValueKind.String ? val.GetString() : val.ToString()
                : null;
        }

        var fullName = GetField("FullName")
            ?? $"{GetField("FirstName") ?? ""} {GetField("LastName") ?? ""}".Trim();
        var phone = GetField("PhoneNumber");

        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(phone))
            return null;

        return new CaptureLeadCommand(
            TenantId:          tenantId,
            FullName:          fullName,
            PhoneNumber:       phone,
            Source:             Domain.LeadSource.Partner,
            InterestedProduct: GetField("InterestedProduct") ?? "Unknown",
            PreferredLanguage: GetField("PreferredLanguage") ?? "fr",
            Latitude:          0,
            Longitude:         0,
            PreferredAgencyId: source.DefaultAgencyId,
            FirstName:         GetField("FirstName"),
            LastName:          GetField("LastName"),
            Email:             GetField("Email"),
            AgencyId:          source.DefaultAgencyId);
    }
}
