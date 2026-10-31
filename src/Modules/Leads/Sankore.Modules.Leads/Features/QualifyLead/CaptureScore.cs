namespace Sankore.Modules.Leads.Features.QualifyLead;

using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// The first score of a lead, at its capture and before its dispatch (lead temperature, lot 2):
/// its source, profile, address and location are known from the first second, and that is when
/// a hot lead must be told apart — not after an agent has worked it. Before, every new lead was
/// « Inconnu » at 0 until its qualification.
///
/// <para>
/// A score, never a qualification: the status does not move (qualifying is the agent's work) and
/// no « score changed » event is published — a first score is not a change, and
/// <c>LeadScoreCriticallyChangedIntegrationEvent</c> would cross the 40 threshold on most captures.
/// The lead is scored as it stands when this is called: call it once every capture field is set.
/// </para>
/// </summary>
internal static class CaptureScore
{
    public const string TriggerEvent = "LEAD_CAPTURED";

    public static async Task ApplyAsync(LeadsDbContext db, Lead lead, CancellationToken ct)
    {
        var calculator = new LeadScoreCalculator(db);
        var (score, factorsJson) = await calculator.CalculateAsync(lead, ct);
        lead.RecordScoreUpdate(score);
        lead.UpdateIntentLevel(await calculator.IntentLevelOfAsync(lead, score, ct));
        db.ScoreHistories.Add(ScoreHistory.Create(
            tenantId:     lead.TenantId,
            leadId:       lead.Id,
            score:        score,
            triggerEvent: TriggerEvent,
            factorsJson:  factorsJson));
    }
}
