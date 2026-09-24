namespace Sankore.Modules.Leads.Features.Ingestion.Pull.DryRun;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DryRunHandler(
    LeadsDbContext db,
    GenericRestPuller puller,
    TimeProvider clock)
    : IRequestHandler<DryRunCommand, Result<DryRunResult>>
{
    private const int MaxResponsePreviewBytes = 100 * 1024; // 100 KB

    public async Task<Result<DryRunResult>> Handle(
        DryRunCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail<DryRunResult>("SOURCE_NOT_FOUND");

        if (source.Status is not (LeadSourceStatus.Draft or LeadSourceStatus.Testing or LeadSourceStatus.Active))
            return Result.Fail<DryRunResult>("SOURCE_NOT_ELIGIBLE_FOR_DRY_RUN");

        var settings = source.Settings as ScheduledPullSettings;
        if (settings is null)
            return Result.Fail<DryRunResult>("NO_PULL_SETTINGS");

        // Override settings for dry run: single page, 30s timeout
        var drySettings = settings with
        {
            MaxPagesPerRun = 1,
            TimeoutSeconds = 30
        };

        var now = clock.GetUtcNow();

        // Fetch single page
        var pullResult = await puller.FetchAsync(source, drySettings, source.LastPullAt, ct);

        // Build request info (secrets masked)
        var requestInfo = new DryRunRequestInfo(
            Url: MaskSecrets(settings.EndpointUrl),
            Method: settings.HttpMethod,
            AuthType: settings.AuthType.ToString());

        // Truncate raw response
        string? rawPreview = null;
        if (pullResult.Items.Count > 0)
        {
            var raw = JsonSerializer.Serialize(pullResult.Items);
            rawPreview = raw.Length > MaxResponsePreviewBytes
                ? raw[..MaxResponsePreviewBytes] + "... [truncated]"
                : raw;
        }

        // Simulate mapping without ingesting
        var previews = new List<DryRunLeadPreview>();
        int mappingErrors = 0;

        foreach (var item in pullResult.Items)
        {
            var preview = SimulateMapping(item, settings);
            previews.Add(preview);
            if (preview.Error is not null) mappingErrors++;
        }

        // Record DryRun run (satisfies NoSuccessfulTest prerequisite)
        var run = LeadSourceRun.Start(source.TenantId, source.Id, clock, LeadSourceRunType.DryRun);

        if (pullResult.Success)
            run.Complete(pullResult.Items.Count, 0, mappingErrors, 0, clock);
        else
            run.Fail(pullResult.Error ?? "DRY_RUN_FAILED", clock);

        db.LeadSourceRuns.Add(run);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new DryRunResult(
            Request: requestInfo,
            RawResponseTruncated: rawPreview,
            SimulatedLeads: previews,
            TotalFetched: pullResult.Items.Count,
            MappingErrors: mappingErrors));
    }

    private static DryRunLeadPreview SimulateMapping(
        JsonElement item, ScheduledPullSettings settings)
    {
        var rules = settings.FieldMappings;
        if (rules is null or { Count: 0 })
            return new(null, null, null, null, "NO_FIELD_MAPPING");

        var mapped = FieldMappingEngine.Apply(rules, item.GetRawText());

        string? GetField(string leadField)
            => mapped.MappedValues.TryGetValue(leadField, out var value) ? value : null;

        var fullName = GetField("fullName")
            ?? $"{GetField("firstName") ?? ""} {GetField("lastName") ?? ""}".Trim();
        var phone = GetField("phoneNumber");
        var email = GetField("email");

        string? externalId = null;
        if (settings.ExternalIdPath is not null)
        {
            var el = item;
            foreach (var seg in settings.ExternalIdPath.TrimStart('$', '.').Split('.'))
            {
                if (!el.TryGetProperty(seg, out el)) { el = default; break; }
            }
            externalId = el.ValueKind != JsonValueKind.Undefined ? el.ToString() : null;
        }

        // Surface transformation failures (bad phone, unresolvable JSONPath, …) —
        // seeing them is the point of a dry run
        string? error = mapped.Errors.Count > 0
            ? string.Join("; ", mapped.Errors.Select(e => $"{e.JsonPath}: {e.Message}"))
            : null;

        if (error is null && string.IsNullOrWhiteSpace(fullName)) error = "MISSING_FULL_NAME";
        else if (error is null && string.IsNullOrWhiteSpace(phone)) error = "MISSING_PHONE_NUMBER";

        return new(fullName, phone, email, externalId, error);
    }

    private static string MaskSecrets(string url)
    {
        // Mask any query params that look like keys/tokens
        if (!url.Contains('?')) return url;
        var parts = url.Split('?', 2);
        return parts[0] + "?[parameters masked]";
    }
}
