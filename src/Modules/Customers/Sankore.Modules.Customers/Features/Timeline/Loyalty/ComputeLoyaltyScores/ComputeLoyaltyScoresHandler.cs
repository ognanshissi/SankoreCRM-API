namespace Sankore.Modules.Customers.Features.Timeline.Loyalty.ComputeLoyaltyScores;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ComputeLoyaltyScoresHandler(
    CustomersDbContext db,
    ICustomerSettings settings,
    TimeProvider clock,
    ILogger<ComputeLoyaltyScoresHandler> logger)
    : IRequestHandler<ComputeLoyaltyScoresCommand, Result<ComputeLoyaltyScoresResult>>
{
    /// <summary>Tenure saturates at 5 years — beyond that, seniority stops discriminating.</summary>
    private const double FullTenureDays = 365.25 * 5;

    /// <summary>Regularity saturates at one timeline fact per month over the trailing year.</summary>
    private const int FullRegularityFacts = 12;

    /// <summary>A client younger than this is scored, but the score is flagged provisional.</summary>
    private const int ProvisionalWindowDays = 90;

    private const string ModuleNotAvailable = "MODULE_NOT_AVAILABLE";

    /// <summary>
    /// camelCase, because <c>BreakdownJson</c> is handed to the front-end verbatim by
    /// <c>GET clients/{clientId}/loyalty-score</c> — it must read like the rest of the API.
    /// </summary>
    private static readonly JsonSerializerOptions BreakdownJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    public async Task<Result<ComputeLoyaltyScoresResult>> Handle(
        ComputeLoyaltyScoresCommand request, CancellationToken ct)
    {
        var tenantId = request.TenantId;
        var now = clock.GetUtcNow();

        var weightsJson = await settings.GetStringAsync(
            tenantId, CustomerSettingKeys.LoyaltyWeightsJson, ct);

        var weights = LoyaltyWeights.Parse(weightsJson, out var weightsError);

        if (weightsError is not null)
        {
            logger.LogWarning(
                "Tenant {TenantId} has a malformed {Key} ({Error}) — factory weights used instead.",
                tenantId, CustomerSettingKeys.LoyaltyWeightsJson, weightsError);
        }

        // Hangfire job: ambient filters cannot be trusted, hence IgnoreQueryFilters + explicit
        // tenant predicate on every query.
        var clients = await db.Clients
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.Status != ClientStatus.Archived
                     && c.Status != ClientStatus.Merged)
            .ToListAsync(ct);

        if (clients.Count == 0)
            return Result.Ok(new ComputeLoyaltyScoresResult(0, 0, ["volume", "products"]));

        var clientIds = clients.Select(c => c.Id).ToList();
        var since = now.AddMonths(-12);

        // Regularity is measured on the timeline — the only interaction history M01 owns.
        var factCounts = await db.ClientTimelineEntries
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                     && clientIds.Contains(e.ClientId)
                     && e.OccurredAt >= since)
            .GroupBy(e => e.ClientId)
            .Select(g => new { ClientId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ClientId, x => x.Count, ct);

        var provisional = 0;

        foreach (var client in clients)
        {
            var tenureDays = Math.Max(0d, (now - client.CreatedAt).TotalDays);
            var tenureRaw = Math.Clamp(tenureDays / FullTenureDays, 0d, 1d);

            var facts = factCounts.TryGetValue(client.Id, out var count) ? count : 0;
            var regularityRaw = Math.Clamp((double)facts / FullRegularityFacts, 0d, 1d);

            // Normalize over the weights that are actually computable — see LoyaltyWeights for
            // why the missing weights leave the denominator instead of scoring zero.
            var available = weights.AvailableWeightTotal;
            var tenurePoints = available == 0 ? 0d : tenureRaw * weights.Tenure * 100d / available;
            var regularityPoints = available == 0 ? 0d : regularityRaw * weights.Regularity * 100d / available;

            var score = (int)Math.Round(Math.Clamp(tenurePoints + regularityPoints, 0d, 100d),
                MidpointRounding.AwayFromZero);

            var isProvisional = client.CreatedAt > now.AddDays(-ProvisionalWindowDays);
            if (isProvisional) provisional++;

            var breakdown = new[]
            {
                new LoyaltyComponentBreakdown("tenure", weights.Tenure, Math.Round(tenureRaw, 4),
                    Math.Round(tenurePoints, 2), IsAvailable: true, UnavailableReason: null),
                new LoyaltyComponentBreakdown("regularity", weights.Regularity, Math.Round(regularityRaw, 4),
                    Math.Round(regularityPoints, 2), IsAvailable: true, UnavailableReason: null),
                // M03 (Savings) owns transaction volume. Recorded as unavailable rather than
                // omitted, so the UI can explain the score instead of hiding half of it.
                new LoyaltyComponentBreakdown("volume", weights.Volume, null, 0d,
                    IsAvailable: false, UnavailableReason: ModuleNotAvailable),
                // M04 (Credit) owns product holdings. Same reasoning.
                new LoyaltyComponentBreakdown("products", weights.Products, null, 0d,
                    IsAvailable: false, UnavailableReason: ModuleNotAvailable),
            };

            var breakdownJson = JsonSerializer.Serialize(new
            {
                computedAt = now,
                score,
                isProvisional,
                provisionalWindowDays = ProvisionalWindowDays,
                tenureDays = (int)tenureDays,
                timelineFactsLast12Months = facts,
                // All four components are ALWAYS present, in this fixed order. An absent key
                // would be indistinguishable from a component that scored zero on its own
                // merits, so the two that no module can compute yet are written out with
                // isAvailable=false and a reason. The flat list below repeats them at the top
                // level, so a reader does not have to walk the array to answer "what was
                // missing when this score was computed".
                components = breakdown,
                unavailableComponents = breakdown
                    .Where(c => !c.IsAvailable)
                    .Select(c => c.Component)
                    .ToArray(),
            }, BreakdownJsonOptions);

            db.ClientLoyaltyScores.Add(ClientLoyaltyScore.Record(
                tenantId, client.Id, score, isProvisional, breakdownJson, now));

            client.SetLoyaltyScore(score, isProvisional, now);
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Loyalty scoring for tenant {TenantId}: {Count} client(s) scored ({Provisional} provisional). "
            + "Components volume and products scored 0 and are flagged unavailable until M03/M04 expose a contract.",
            tenantId, clients.Count, provisional);

        return Result.Ok(new ComputeLoyaltyScoresResult(clients.Count, provisional, ["volume", "products"]));
    }
}
