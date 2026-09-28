namespace Sankore.Modules.Customers.Features.Timeline.Segments;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// One segmentation rule, as stored in the tenant setting <c>segment-rules-json</c>
/// (US-M01-BE-27).
///
/// Rules are evaluated by ASCENDING <see cref="Priority"/> and the FIRST match wins, so a
/// rule set is an ordered decision list, not a scoring system: two rules that could both
/// match a client are resolved by priority, never by "best fit".
///
/// Every criterion is optional; an omitted criterion is simply not tested. A rule with no
/// criterion at all therefore matches everyone — which is exactly how a catch-all
/// "STANDARD" segment is expressed, given the highest priority number.
/// </summary>
/// <param name="Code">Stable identifier of the rule, unique in the tenant. Appears in <c>ClientSegmentHistory.RuleCode</c>.</param>
/// <param name="Priority">Evaluation order, ascending. Lower wins.</param>
/// <param name="SegmentCode">Segment assigned when the rule matches, e.g. <c>PREMIUM</c>.</param>
/// <param name="MinTenureDays">Minimum days since the client record was created (inclusive).</param>
/// <param name="MaxTenureDays">Maximum days since the client record was created (inclusive).</param>
/// <param name="MaxDaysSinceLastActivity">Client must have a timeline fact at most this many days old.</param>
/// <param name="MinDaysSinceLastActivity">Client must have been quiet for at least this many days (a client with no fact at all satisfies this).</param>
/// <param name="RequiredKycStatus">Exact KYC status name, e.g. <c>Approved</c>. Case-insensitive.</param>
/// <param name="RequiredRiskLevel">Exact risk level name, e.g. <c>Low</c>. Case-insensitive.</param>
/// <param name="RequiresOutstandingData">
/// True when the rule needs outstanding balances / product holdings — data owned by M03
/// (Savings) and M04 (Credit), which do not expose a contract yet. Such a rule is SKIPPED,
/// not guessed at: see <see cref="SegmentRuleEvaluation"/>.
/// </param>
public sealed record SegmentRuleDefinition(
    string Code,
    int Priority,
    string SegmentCode,
    int? MinTenureDays = null,
    int? MaxTenureDays = null,
    int? MaxDaysSinceLastActivity = null,
    int? MinDaysSinceLastActivity = null,
    string? RequiredKycStatus = null,
    string? RequiredRiskLevel = null,
    bool RequiresOutstandingData = false);

/// <summary>
/// Facts about a client needed to evaluate a rule. Built once per client by the
/// segmentation handler, so no rule ever hits the database on its own.
/// </summary>
/// <param name="LastActivityAt">Most recent timeline fact, or <c>null</c> when the client has none.</param>
internal sealed record ClientSegmentFacts(
    Guid ClientId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastActivityAt,
    string KycStatus,
    string RiskLevel,
    string? CurrentSegment);

/// <summary>Rule parsing, evaluability and matching. Pure functions — trivially testable.</summary>
internal static class SegmentRuleEvaluation
{
    /// <summary>Reason code returned by <c>GET clients/segments/rules</c> for a skipped rule.</summary>
    internal const string OutstandingDataUnavailableReason = "OUTSTANDING_DATA_UNAVAILABLE";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parses the tenant setting. Returns an empty list on malformed JSON rather than
    /// throwing: a bad rule set must not take the nightly job down, and the caller logs it.
    /// </summary>
    internal static bool TryParse(string? json, out List<SegmentRuleDefinition> rules, out string? error)
    {
        rules = [];
        error = null;

        if (string.IsNullOrWhiteSpace(json)) return true;

        try
        {
            rules = JsonSerializer.Deserialize<List<SegmentRuleDefinition>>(json, JsonOptions) ?? [];
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static string Serialize(IEnumerable<SegmentRuleDefinition> rules)
        => JsonSerializer.Serialize(rules.OrderBy(r => r.Priority).ToList(), JsonOptions);

    /// <summary>
    /// False while the rule depends on data no module exposes yet. Such a rule is reported
    /// as inactive by <c>GET clients/segments/rules</c> and skipped (with a log line) by the
    /// nightly job — it is never evaluated as if the missing data were zero, because that
    /// would silently classify every client into the wrong segment.
    /// </summary>
    internal static bool IsEvaluable(this SegmentRuleDefinition rule) => !rule.RequiresOutstandingData;

    internal static string? NotEvaluableReason(this SegmentRuleDefinition rule)
        => rule.RequiresOutstandingData ? OutstandingDataUnavailableReason : null;

    /// <summary>Tests an evaluable rule against one client. Never call on a non-evaluable rule.</summary>
    internal static bool Matches(this SegmentRuleDefinition rule, ClientSegmentFacts facts, DateTimeOffset now)
    {
        var tenureDays = (int)Math.Floor((now - facts.CreatedAt).TotalDays);

        if (rule.MinTenureDays is { } minTenure && tenureDays < minTenure) return false;
        if (rule.MaxTenureDays is { } maxTenure && tenureDays > maxTenure) return false;

        if (rule.MaxDaysSinceLastActivity is { } maxIdle)
        {
            // No activity at all can never satisfy "active in the last N days".
            if (facts.LastActivityAt is not { } last) return false;
            if ((int)Math.Floor((now - last).TotalDays) > maxIdle) return false;
        }

        if (rule.MinDaysSinceLastActivity is { } minIdle)
        {
            // A client who never produced a fact is dormant by definition, so it passes.
            if (facts.LastActivityAt is { } last
                && (int)Math.Floor((now - last).TotalDays) < minIdle) return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.RequiredKycStatus)
            && !string.Equals(rule.RequiredKycStatus.Trim(), facts.KycStatus, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.RequiredRiskLevel)
            && !string.Equals(rule.RequiredRiskLevel.Trim(), facts.RiskLevel, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// First matching evaluable rule in priority order, or <c>null</c> when nothing matches
    /// (the client then keeps the segment it already has — segmentation never clears one).
    /// </summary>
    internal static SegmentRuleDefinition? FirstMatch(
        IReadOnlyList<SegmentRuleDefinition> orderedEvaluableRules, ClientSegmentFacts facts, DateTimeOffset now)
    {
        foreach (var rule in orderedEvaluableRules)
            if (rule.Matches(facts, now)) return rule;

        return null;
    }
}
