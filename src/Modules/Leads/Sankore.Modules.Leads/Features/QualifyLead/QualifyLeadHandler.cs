namespace Sankore.Modules.Leads.Features.QualifyLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class QualifyLeadHandler(LeadsDbContext db, LeadScoreCalculator calculator)
    : IRequestHandler<QualifyLeadCommand, Result<QualifyLeadResult>>
{
    public async Task<Result<QualifyLeadResult>> Handle(
        QualifyLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<QualifyLeadResult>("LEAD_NOT_FOUND");

        // Determine score — explicit or auto-calculated.
        string factorsJson;
        int score;

        if (cmd.Score.HasValue)
        {
            score       = cmd.Score.Value;
            factorsJson = "{}";
        }
        else
        {
            (score, factorsJson) = calculator.Calculate(lead);
        }

        var qualifyResult = lead.Qualify(score);
        if (qualifyResult.IsFailure)
            return Result.Fail<QualifyLeadResult>(qualifyResult.Error!);

        // Record the immutable audit entry.
        var history = ScoreHistory.Create(
            tenantId:     lead.TenantId,
            leadId:       lead.Id,
            score:        score,
            triggerEvent: cmd.TriggerEvent,
            factorsJson:  factorsJson);

        db.ScoreHistories.Add(history);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new QualifyLeadResult(
            LeadId:      lead.Id,
            Score:       score,
            Status:      lead.Status.ToString(),
            FactorsJson: factorsJson));
    }
}
