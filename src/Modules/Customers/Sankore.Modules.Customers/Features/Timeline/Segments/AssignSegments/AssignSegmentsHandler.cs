namespace Sankore.Modules.Customers.Features.Timeline.Segments.AssignSegments;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class AssignSegmentsHandler(
    CustomersDbContext db,
    ICustomerSettings settings,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher,
    TimeProvider clock,
    ILogger<AssignSegmentsHandler> logger)
    : IRequestHandler<AssignSegmentsCommand, Result<AssignSegmentsResult>>
{
    public async Task<Result<AssignSegmentsResult>> Handle(
        AssignSegmentsCommand request, CancellationToken ct)
    {
        var tenantId = request.TenantId;
        var now = clock.GetUtcNow();

        var json = await settings.GetStringAsync(tenantId, CustomerSettingKeys.SegmentRulesJson, ct);

        if (!SegmentRuleEvaluation.TryParse(json, out var rules, out var parseError))
        {
            logger.LogError(
                "Tenant {TenantId} has a malformed {Key}: {Error}. Segmentation skipped — no client touched.",
                tenantId, CustomerSettingKeys.SegmentRulesJson, parseError);

            return Result.Ok(new AssignSegmentsResult(0, 0, 0, 0));
        }

        var ordered = rules.OrderBy(r => r.Priority).ToList();
        var evaluable = ordered.Where(r => r.IsEvaluable()).ToList();
        var skipped = ordered.Where(r => !r.IsEvaluable()).ToList();

        if (skipped.Count > 0)
        {
            // Once per execution, as required: a rule depending on outstanding balances or
            // product holdings stays INACTIVE until M03 (Savings) / M04 (Credit) publish a
            // contract. Evaluating it with zeroed data would misclassify every client, so it
            // is skipped instead — and said out loud, because a silently inactive rule is a
            // segmentation bug nobody notices.
            logger.LogWarning(
                "Tenant {TenantId}: {SkippedCount} segmentation rule(s) ignored — they require "
                + "outstanding/product data that no module exposes yet ({Codes}). They stay inactive "
                + "and are reported as IsEvaluable=false by GET clients/segments/rules.",
                tenantId, skipped.Count, string.Join(", ", skipped.Select(r => r.Code)));
        }

        if (evaluable.Count == 0)
            return Result.Ok(new AssignSegmentsResult(0, 0, 0, skipped.Count));

        // Hangfire job: no ambient tenant filter can be trusted, so bypass it and re-apply
        // the predicate by hand on every query below.
        var clients = await db.Clients
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.Status != ClientStatus.Archived
                     && c.Status != ClientStatus.Merged)
            .ToListAsync(ct);

        if (clients.Count == 0)
            return Result.Ok(new AssignSegmentsResult(0, 0, evaluable.Count, skipped.Count));

        var clientIds = clients.Select(c => c.Id).ToList();

        // Last activity comes from the timeline itself — the read model this zone owns, which
        // is precisely why segmentation lives here and not in the Clients zone.
        var lastActivity = await db.ClientTimelineEntries
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && clientIds.Contains(e.ClientId))
            .GroupBy(e => e.ClientId)
            .Select(g => new { ClientId = g.Key, LastAt = g.Max(e => e.OccurredAt) })
            .ToDictionaryAsync(x => x.ClientId, x => x.LastAt, ct);

        var openHistories = await db.ClientSegmentHistories
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId && h.ValidTo == null && clientIds.Contains(h.ClientId))
            .ToListAsync(ct);

        var openByClient = openHistories
            .GroupBy(h => h.ClientId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.ValidFrom).First());

        var changed = 0;

        foreach (var client in clients)
        {
            var facts = new ClientSegmentFacts(
                ClientId: client.Id,
                CreatedAt: client.CreatedAt,
                LastActivityAt: lastActivity.TryGetValue(client.Id, out var last) ? last : null,
                KycStatus: client.KycStatus.ToString(),
                RiskLevel: client.RiskLevel.ToString(),
                CurrentSegment: client.SegmentCode);

            var match = SegmentRuleEvaluation.FirstMatch(evaluable, facts, now);

            // No rule matched: the client keeps whatever segment it has. Segmentation
            // assigns, it never un-assigns — losing a segment would silently drop a client
            // out of every campaign built on it.
            if (match is null) continue;

            if (string.Equals(match.SegmentCode, client.SegmentCode, StringComparison.Ordinal))
                continue;

            var previous = client.SegmentCode;

            // Close the running history row, open the new one: the segment column on the
            // client is a cache, this table is the auditable truth.
            if (openByClient.TryGetValue(client.Id, out var open))
                open.Close(now);

            db.ClientSegmentHistories.Add(ClientSegmentHistory.Open(
                tenantId, client.Id, match.SegmentCode, now, match.Code));

            client.SetSegment(match.SegmentCode, now);

            await publisher.PublishAsync(
                new ClientSegmentChangedEvent(tenantId, client.Id, previous, match.SegmentCode), ct);

            changed++;
        }

        // Publisher writes to the outbox in this same transaction — save once, at the end.
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Segmentation for tenant {TenantId}: {Evaluated} client(s) evaluated, {Changed} segment(s) "
            + "changed, {Applied} rule(s) applied, {Skipped} rule(s) inactive.",
            tenantId, clients.Count, changed, evaluable.Count, skipped.Count);

        return Result.Ok(new AssignSegmentsResult(clients.Count, changed, evaluable.Count, skipped.Count));
    }
}
