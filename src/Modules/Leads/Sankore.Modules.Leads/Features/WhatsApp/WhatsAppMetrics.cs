namespace Sankore.Modules.Leads.Features.WhatsApp;

using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>The WhatsApp indicators of a period (WA-BE-22), within the caller's perimeter.</summary>
/// <param name="ConversationsWithInbound">Conversations in which a customer wrote during the period — the base of the escalation rate.</param>
/// <param name="TemplateResponseRate">Templates answered by the customer within 24 hours, over templates sent. Null with no template.</param>
/// <param name="EscalationRate">Handovers to a person over conversations with a customer message.</param>
/// <param name="WithinSlaRate">Of the handovers that had a takeover deadline and were settled, the share taken over in time.</param>
/// <param name="BotResolutionRate">Conversations the bot answered with no handover to a person, over conversations the bot answered.</param>
/// <param name="EstimatedTotalCost">Null when a category that was used has no price configured: a partial total would read as complete.</param>
public sealed record WhatsAppMetricsDto(
    DateTimeOffset From,
    DateTimeOffset To,
    int ConversationsWithInbound,
    int TemplatesSent,
    int TemplatesAnswered,
    double? TemplateResponseRate,
    int Escalations,
    double? EscalationRate,
    int TakenOver,
    double? AverageTakeoverMinutes,
    double? MedianTakeoverMinutes,
    int Breaches,
    double? WithinSlaRate,
    int BotConversations,
    int BotResolved,
    double? BotResolutionRate,
    IReadOnlyList<WhatsAppMetricReasonDto> EscalationsByReason,
    IReadOnlyList<WhatsAppMetricCostDto> CostByCategory,
    string Currency,
    decimal? EstimatedTotalCost);

public sealed record WhatsAppMetricReasonDto(string Reason, string Label, int Count);

/// <param name="Category">Meta's category — <c>MARKETING</c>, <c>UTILITY</c>, <c>AUTHENTICATION</c> — or <c>SERVICE</c> for free replies inside the window.</param>
/// <param name="UnitPrice">From the tenant setting; null when not configured.</param>
public sealed record WhatsAppMetricCostDto(string Category, int Messages, decimal? UnitPrice, decimal? EstimatedCost);

internal static class WhatsAppMetricsErrors
{
    public const string PeriodInvalid = "WHATSAPP_METRICS_PERIOD_INVALID";
}

internal sealed record GetWhatsAppMetricsQuery(DateTimeOffset? From, DateTimeOffset? To) : IRequest<Result<WhatsAppMetricsDto>>;

/// <summary>
/// WA-BE-22, computed on read from the messages, the bot turns and the handover history — no
/// counter to drift, and a period of any length (a year at most).
///
/// <para>
/// <b>Within the perimeter</b> of <see cref="WhatsAppPerimeter"/> as a supervisor sees it: a
/// branch manager's figures are their team's, the bypass roles' the tenant's. Figures that mixed
/// in another branch's conversations would be compared against a team that did not produce them.
/// </para>
///
/// <para>
/// <b>Definitions that are choices, stated.</b> A template is « answered » when the customer writes
/// in the same conversation within 24 hours. A conversation is « resolved by the bot » when the bot
/// answered in it during the period and it was not handed to a person during the period — the
/// customer may simply have stopped writing, which is the most the data can say. The cost is
/// Meta's per-message model: templates by category at the tenant's configured price, free replies
/// inside the window counted under <c>SERVICE</c>; no price is invented.
/// </para>
/// </summary>
internal sealed class GetWhatsAppMetricsHandler(
    LeadsDbContext db, WhatsAppPerimeter perimeter, ICurrentUser me, ILeadSettings settings, TimeProvider clock)
    : IRequestHandler<GetWhatsAppMetricsQuery, Result<WhatsAppMetricsDto>>
{
    private static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(366);

    public async Task<Result<WhatsAppMetricsDto>> Handle(GetWhatsAppMetricsQuery query, CancellationToken ct)
    {
        var to = query.To ?? clock.GetUtcNow();
        var from = query.From ?? to.AddDays(-30);

        if (from >= to || to - from > MaxPeriod) return Result.Fail<WhatsAppMetricsDto>(WhatsAppMetricsErrors.PeriodInvalid);

        var scope = (await perimeter.ScopeAsync(maySupervise: true, ct)).Select(x => x.Conversation.Id);

        var inPeriod = db.WhatsAppMessages.Where(m => scope.Contains(m.ConversationId) && m.OccurredAt >= from && m.OccurredAt < to);

        var conversationsWithInbound = await inPeriod
            .Where(m => m.Direction == WhatsAppDirection.Inbound)
            .Select(m => m.ConversationId)
            .Distinct()
            .CountAsync(ct);

        // ── Templates ────────────────────────────────────────────────────────
        var templates = await inPeriod
            .Where(m => m.Direction == WhatsAppDirection.Outbound
                     && m.Kind == WhatsAppMessageKind.Template
                     && m.Status != WhatsAppMessageStatus.Failed)
            .Select(t => new
            {
                t.TemplateId,
                Answered = db.WhatsAppMessages.Any(i => i.ConversationId == t.ConversationId
                                                     && i.Direction == WhatsAppDirection.Inbound
                                                     && i.OccurredAt > t.OccurredAt
                                                     && i.OccurredAt <= t.OccurredAt.AddHours(24)),
            })
            .ToListAsync(ct);

        var freeReplies = await inPeriod.CountAsync(m => m.Direction == WhatsAppDirection.Outbound
                                                      && m.Kind != WhatsAppMessageKind.Template
                                                      && m.Status != WhatsAppMessageStatus.Failed, ct);

        // ── Handovers ────────────────────────────────────────────────────────
        var handovers = await db.WhatsAppHandovers
            .Where(h => scope.Contains(h.ConversationId) && h.EscalatedAt >= from && h.EscalatedAt < to)
            .Select(h => new { h.ConversationId, h.Reason, h.FromBot, h.EscalatedAt, h.EndedAt, h.EndedBy, h.BreachedAt, h.TakeoverDeadline })
            .ToListAsync(ct);

        var delays = handovers
            .Where(h => h.EndedBy == WhatsAppHandoverEnd.TakenOver && h.EndedAt is not null)
            .Select(h => (h.EndedAt!.Value - h.EscalatedAt).TotalMinutes)
            .OrderBy(m => m)
            .ToList();

        var settled = handovers
            .Where(h => h.TakeoverDeadline is not null && (h.EndedBy == WhatsAppHandoverEnd.TakenOver || h.BreachedAt is not null))
            .ToList();

        var inTime = settled.Count(h => h.BreachedAt is null && h.EndedAt <= h.TakeoverDeadline);

        // ── Bot ──────────────────────────────────────────────────────────────
        var botConversations = await db.WhatsAppBotTurns
            .Where(t => scope.Contains(t.ConversationId) && t.CreatedAt >= from && t.CreatedAt < to
                     && t.Outcome == WhatsAppBotTurnOutcome.Replied)
            .Select(t => t.ConversationId)
            .Distinct()
            .ToListAsync(ct);

        var handedOver = handovers.Where(h => h.FromBot).Select(h => h.ConversationId).ToHashSet();
        var botResolved = botConversations.Count(id => !handedOver.Contains(id));

        // ── Cost ─────────────────────────────────────────────────────────────
        var templateIds = templates.Where(t => t.TemplateId is not null).Select(t => t.TemplateId!.Value).Distinct().ToList();

        var categories = templateIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.WhatsAppTemplates
                .Where(t => templateIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Category.ToUpperInvariant(), ct);

        var prices = ParsePrices(await settings.GetStringAsync(me.TenantId, LeadSettingKeys.WhatsAppTemplateUnitPrices, ct));
        var currency = await settings.GetStringAsync(me.TenantId, LeadSettingKeys.WhatsAppPriceCurrency, ct);

        var counts = templates
            .GroupBy(t => t.TemplateId is { } id && categories.TryGetValue(id, out var c) ? c : "UNKNOWN")
            .Select(g => (Category: g.Key, Count: g.Count()))
            .ToList();

        if (freeReplies > 0) counts.Add(("SERVICE", freeReplies));

        var cost = counts
            .OrderByDescending(c => c.Count)
            .Select(c =>
            {
                decimal? unit = prices.TryGetValue(c.Category, out var p) ? p : null;
                return new WhatsAppMetricCostDto(c.Category, c.Count, unit, unit * c.Count);
            })
            .ToList();

        return Result.Ok(new WhatsAppMetricsDto(
            from,
            to,
            conversationsWithInbound,
            templates.Count,
            templates.Count(t => t.Answered),
            Rate(templates.Count(t => t.Answered), templates.Count),
            handovers.Count,
            Rate(handovers.Count, conversationsWithInbound),
            delays.Count,
            delays.Count == 0 ? null : Math.Round(delays.Average(), 1),
            delays.Count == 0 ? null : Math.Round(Median(delays), 1),
            handovers.Count(h => h.BreachedAt is not null),
            Rate(inTime, settled.Count),
            botConversations.Count,
            botResolved,
            Rate(botResolved, botConversations.Count),
            handovers
                .GroupBy(h => h.Reason)
                .Select(g => new WhatsAppMetricReasonDto(g.Key, Bot.WhatsAppBotReasons.Label(g.Key), g.Count()))
                .OrderByDescending(r => r.Count)
                .ToList(),
            cost,
            string.IsNullOrWhiteSpace(currency) ? "XOF" : currency.Trim(),
            cost.Count > 0 && cost.All(c => c.EstimatedCost is not null) ? cost.Sum(c => c.EstimatedCost) : null));
    }

    private static double? Rate(int part, int whole) => whole == 0 ? null : Math.Round((double)part / whole, 4);

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    /// <summary>
    /// <c>{"MARKETING": 45, "UTILITY": "10,5"}</c> → prices by upper-case category. A malformed
    /// entry is skipped: a typo in one category must not blank the whole cost table.
    /// </summary>
    internal static IReadOnlyDictionary<string, decimal> ParsePrices(string? json)
    {
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return prices;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return prices;

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                decimal? value = entry.Value.ValueKind switch
                {
                    JsonValueKind.Number when entry.Value.TryGetDecimal(out var d) => d,
                    JsonValueKind.String when decimal.TryParse(entry.Value.GetString()?.Replace(',', '.'),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
                    _ => null,
                };

                if (value is >= 0) prices[entry.Name.Trim().ToUpperInvariant()] = value.Value;
            }
        }
        catch (JsonException)
        {
            // An unreadable setting reads as « no price », never as a failed dashboard.
        }

        return prices;
    }
}
