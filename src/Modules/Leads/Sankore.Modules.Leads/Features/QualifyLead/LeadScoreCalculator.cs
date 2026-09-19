namespace Sankore.Modules.Leads.Features.QualifyLead;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Computes a 0-100 lead score from five AC-mandated dimensions.
///
/// Scoring breakdown:
///   1. Profile completeness   0-25 pts  (identity richness + qualification form coverage)
///   2. Source quality         0-20 pts  (acquisition channel reliability)
///   3. Interactions           0-20 pts  (activity volume and recency)
///   4. Behaviour              0-15 pts  (engagement quality — positive outcomes, multi-channel)
///   5. Declarative coherence  0-10 pts  (consistency cross-checks on declared data)
///   6. Location               0-10 pts  (geo-point + preferred agency)
///
/// Total max: 100 pts. Score is clamped to [0, 100].
/// Each dimension is serialised individually into FactorsJson for explainability.
/// </summary>
internal sealed class LeadScoreCalculator(LeadsDbContext db)
{
    public async Task<(int Score, string FactorsJson)> CalculateAsync(Lead lead, CancellationToken ct)
    {
        // Load activities once — shared by Interactions and Behaviour dimensions.
        var activities = await db.LeadActivities
            .Where(a => a.LeadId == lead.Id)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;

        var profile     = ProfileCompletenessScore(lead);
        var source      = SourceQualityScore(lead);
        var interaction = InteractionScore(activities, now);
        var behaviour   = BehaviourScore(activities);
        var coherence   = DeclarativeCoherenceScore(lead);
        var location    = LocationScore(lead);

        var total = Math.Clamp(profile + source + interaction + behaviour + coherence + location, 0, 100);

        var factorsJson = JsonSerializer.Serialize(new
        {
            profileCompleteness  = profile,
            sourceQuality        = source,
            interactions         = interaction,
            behaviour            = behaviour,
            declarativeCoherence = coherence,
            location             = location,
            total                = total
        });

        return (total, factorsJson);
    }

    // ── 1. Profile completeness (0-25) ────────────────────────────────────────
    // Rewards rich identity data and how far the qualification form has been filled.

    private static int ProfileCompletenessScore(Lead lead)
    {
        int pts = 0;
        if (!string.IsNullOrWhiteSpace(lead.FirstName) && !string.IsNullOrWhiteSpace(lead.LastName)) pts += 4;
        if (!string.IsNullOrWhiteSpace(lead.Email))       pts += 3;
        if (lead.DateOfBirth.HasValue)                    pts += 3;
        if (!string.IsNullOrWhiteSpace(lead.NationalId))  pts += 4;
        if (lead.DesiredAmount is { Amount: > 0 })        pts += 3;
        if (!string.IsNullOrWhiteSpace(lead.CompanyName)) pts += 3;
        // Qualification form completeness (0.0–1.0) → up to 5 pts.
        pts += (int)Math.Round(lead.QualificationCompleteness * 5);
        return Math.Min(pts, 25);
    }

    // ── 2. Source quality (0-20) ──────────────────────────────────────────────
    // In-branch and referred leads carry the highest intent signal.

    private static int SourceQualityScore(Lead lead) => lead.Source switch
    {
        LeadSource.Agency      => 20,
        LeadSource.CallCenter  => 18,
        LeadSource.Referral    => 18,
        LeadSource.WhatsApp    => 15,
        LeadSource.MobileAgent => 15,
        LeadSource.Web         => 12,
        LeadSource.Partner     => 12,
        LeadSource.Sms         => 10,
        LeadSource.Ussd        => 10,
        LeadSource.Campaign    => 10,
        LeadSource.FileImport  => 5,
        _                      => 8
    };

    // ── 3. Interactions (0-20) ────────────────────────────────────────────────
    // More and more-recent interactions indicate an actively-worked lead.

    private static int InteractionScore(IReadOnlyList<LeadActivity> activities, DateTimeOffset now)
    {
        // Volume bucket → 0-12 pts
        int volumePts = activities.Count switch
        {
            0           => 0,
            1           => 3,
            2 or 3      => 6,
            4 or 5 or 6 => 9,
            _           => 12
        };

        // Recency of most recent activity → 0-8 pts
        int recencyPts = 0;
        if (activities.Count > 0)
        {
            var daysSinceLast = (now - activities.Max(a => a.PerformedAt)).TotalDays;
            recencyPts = daysSinceLast switch
            {
                <= 7  => 8,
                <= 30 => 5,
                <= 90 => 2,
                _     => 1
            };
        }

        return volumePts + recencyPts;
    }

    // ── 4. Behaviour — engagement quality (0-15) ──────────────────────────────
    // Positive outcome ratio (0-10) + multi-channel engagement (0-5).

    private static int BehaviourScore(IReadOnlyList<LeadActivity> activities)
    {
        if (activities.Count == 0) return 0;

        // Positive outcomes ratio among activities that have an outcome recorded.
        var withOutcome = activities.Where(a => a.Outcome.HasValue).ToList();
        int outcomePts = 0;
        if (withOutcome.Count > 0)
        {
            var positiveSet = new HashSet<ActivityOutcome>
                { ActivityOutcome.Interested, ActivityOutcome.Completed, ActivityOutcome.Reached };

            var ratio = (double)withOutcome.Count(a => positiveSet.Contains(a.Outcome!.Value))
                        / withOutcome.Count;

            outcomePts = ratio switch
            {
                > 0.75 => 10,
                > 0.50 => 8,
                > 0.25 => 5,
                > 0    => 3,
                _      => 0
            };
        }

        // Multi-channel: ≥2 distinct activity types signals diversified engagement.
        var channelPts = activities.Select(a => a.Type).Distinct().Count() >= 2 ? 5 : 0;

        return outcomePts + channelPts;
    }

    // ── 5. Declarative coherence (0-10) ──────────────────────────────────────
    // Checks that declared data is internally consistent and plausible.

    private static int DeclarativeCoherenceScore(Lead lead)
    {
        int pts = 0;

        // NationalId present and plausible length (5-20 chars).
        if (!string.IsNullOrWhiteSpace(lead.NationalId) && lead.NationalId.Length is >= 5 and <= 20)
            pts += 3;

        // Phone number: 7-15 digits after stripping non-numeric characters.
        var digits = new string(lead.PhoneNumber.Where(char.IsDigit).ToArray());
        if (digits.Length is >= 7 and <= 15)
            pts += 3;

        // Desired amount is declared and positive.
        if (lead.DesiredAmount is { Amount: > 0 })
            pts += 4;

        return pts; // max 10
    }

    // ── 6. Location (0-10) ───────────────────────────────────────────────────

    private static int LocationScore(Lead lead)
    {
        int pts = 0;
        if (lead.Location is not null)       pts += 5;
        if (lead.PreferredAgencyId.HasValue) pts += 5;
        return pts;
    }
}
