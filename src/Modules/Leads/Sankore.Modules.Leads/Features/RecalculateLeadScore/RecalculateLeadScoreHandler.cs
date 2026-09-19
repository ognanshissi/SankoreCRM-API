namespace Sankore.Modules.Leads.Features.RecalculateLeadScore;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Features.RecalculateLeadScore.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class RecalculateLeadScoreHandler(
    LeadsDbContext db,
    LeadScoreCalculator calculator,
    IOptions<LeadModuleSettings> settings,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<RecalculateLeadScoreCommand, Result<RecalculateLeadScoreResult>>
{
    public async Task<Result<RecalculateLeadScoreResult>> Handle(
        RecalculateLeadScoreCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<RecalculateLeadScoreResult>("LEAD_NOT_FOUND");

        // Skip recalculation for closed leads — their score is final.
        if (lead.Status is LeadStatus.Converted or LeadStatus.Archived
                        or LeadStatus.Lost or LeadStatus.Disqualified)
            return Result.Fail<RecalculateLeadScoreResult>("LEAD_IS_CLOSED");

        var previousScore = lead.Score;
        var (newScore, factorsJson) = await calculator.CalculateAsync(lead, ct);

        lead.RecordScoreUpdate(newScore);
        lead.UpdateIntentLevel(DeriveIntentLevel(newScore));

        db.ScoreHistories.Add(ScoreHistory.Create(
            tenantId:     lead.TenantId,
            leadId:       lead.Id,
            score:        newScore,
            triggerEvent: cmd.TriggerEvent,
            factorsJson:  factorsJson));

        // ── Critical change detection ─────────────────────────────────────────
        var cfg          = settings.Value;
        var delta        = newScore - previousScore;
        var criticalChange =
            Math.Abs(delta) >= cfg.CriticalScoreChangeDelta ||
            CrossesThreshold(previousScore, newScore, cfg.CriticalScoreThresholds);

        if (criticalChange)
        {
            await publisher.PublishAsync(
                new LeadScoreCriticallyChangedIntegrationEvent(
                    LeadId:        lead.Id,
                    TenantId:      lead.TenantId,
                    PreviousScore: previousScore,
                    NewScore:      newScore,
                    Delta:         delta,
                    TriggerEvent:  cmd.TriggerEvent),
                ct);
        }

        await db.SaveChangesAsync(ct);

        return Result.Ok(new RecalculateLeadScoreResult(
            LeadId:        lead.Id,
            PreviousScore: previousScore,
            NewScore:      newScore,
            CriticalChange: criticalChange,
            FactorsJson:   factorsJson));
    }

    private static LeadIntentLevel DeriveIntentLevel(int score) => score switch
    {
        >= 80 => LeadIntentLevel.Hot,
        >= 60 => LeadIntentLevel.Warm,
        >= 40 => LeadIntentLevel.Cold,
        _     => LeadIntentLevel.Unknown
    };

    /// <summary>Returns true if the score crosses any threshold in either direction.</summary>
    private static bool CrossesThreshold(int prev, int next, int[] thresholds)
        => thresholds.Any(t => (prev < t && next >= t) || (prev >= t && next < t));
}
