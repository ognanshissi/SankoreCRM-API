namespace Sankore.Modules.Leads.Features.DismissDuplicate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DismissDuplicateHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<DismissDuplicateCommand, Result>
{
    public async Task<Result> Handle(DismissDuplicateCommand cmd, CancellationToken ct)
    {
        // Verify the lead exists (scoped by tenant via global query filter).
        var leadExists = await db.Leads.AnyAsync(l => l.Id == cmd.LeadId, ct);
        if (!leadExists)
            return Result.Fail("LEAD_NOT_FOUND");

        var candidateExists = await db.Leads.AnyAsync(l => l.Id == cmd.CandidateLeadId, ct);
        if (!candidateExists)
            return Result.Fail("CANDIDATE_LEAD_NOT_FOUND");

        // Idempotent: if this pair was already dismissed, succeed silently.
        var alreadyDismissed = await db.DuplicateDismissals
            .AnyAsync(d =>
                (d.LeadId == cmd.LeadId && d.CandidateLeadId == cmd.CandidateLeadId) ||
                (d.LeadId == cmd.CandidateLeadId && d.CandidateLeadId == cmd.LeadId),
                ct);

        if (alreadyDismissed)
            return Result.Ok();

        var dismissal = DuplicateDismissal.Create(
            tenantId:        cmd.TenantId,
            leadId:          cmd.LeadId,
            candidateLeadId: cmd.CandidateLeadId,
            dismissedBy:     cmd.DismissedBy,
            clock:           clock,
            reason:          cmd.Reason);

        db.DuplicateDismissals.Add(dismissal);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
