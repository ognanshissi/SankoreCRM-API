namespace Sankore.Modules.Leads.Features.NurtureLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Tasks;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class NurtureLeadHandler(LeadsDbContext db, TimeProvider clock, AgentCapacityService? capacity = null)
    : IRequestHandler<NurtureLeadCommand, Result>
{
    public async Task<Result> Handle(NurtureLeadCommand cmd, CancellationToken ct)
    {
        // A lead « en nurturing » with no sequence receives nothing, while the screen says it will.
        if (cmd.SequenceId is not { } sequenceId)
            return Result.Fail("SEQUENCE_REQUIRED");

        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        // ── Consent E03 check (US-M13-150) ──────────────────────────────
        // Marketing consent is mandatory before placing a lead into a nurturing sequence.
        var hasConsent = await db.LeadConsents
            .AnyAsync(c => c.LeadId == cmd.LeadId
                        && c.Type == ConsentType.Marketing
                        && c.Status == ConsentStatus.Active, ct);

        if (!hasConsent)
            return Result.Fail("MARKETING_CONSENT_REQUIRED");

        // Every step is an e-mail: without an address the job would skip this lead for ever.
        if (string.IsNullOrWhiteSpace(lead.Email))
            return Result.Fail("LEAD_HAS_NO_EMAIL");

        var sequence = await db.NurturingSequences
            .Include(s => s.Steps)
            .FirstOrDefaultAsync(s => s.Id == sequenceId && s.IsActive, ct);

        if (sequence is null)
            return Result.Fail("SEQUENCE_NOT_FOUND");

        if (sequence.Steps.Count == 0)
            return Result.Fail("SEQUENCE_HAS_NO_STEPS");

        var activeEnrollments = await db.NurturingEnrollments
            .AsTracking()
            .Where(e => e.LeadId == cmd.LeadId && e.Status == NurturingEnrollmentStatus.Active)
            .ToListAsync(ct);

        if (lead.Status == LeadStatus.Nurturing)
        {
            if (activeEnrollments.Count > 0)
                return Result.Fail("LEAD_ALREADY_ENROLLED");
            // Placed in nurturing without a sequence, as was possible before: it can now get one.
        }
        else
        {
            var result = lead.Nurture();
            if (result.IsFailure)
                return result;

            // The lead had left nurturing: what is still active is an old round the job has not
            // cancelled yet, and it must not stop this one.
            foreach (var stale in activeEnrollments)
                stale.Cancel("LEAD_ENROLLED_AGAIN", clock);
        }

        var agents = await AssignmentTasks.SetAsideAsync(db, lead, clock.GetUtcNow(), ct);

        var firstStep = sequence.Steps.OrderBy(s => s.Order).First();
        db.NurturingEnrollments.Add(NurturingEnrollment.Create(
            tenantId:       lead.TenantId,
            leadId:         lead.Id,
            sequenceId:     sequence.Id,
            firstStepDelay: firstStep.DelayFromPrevious,
            clock:          clock));

        await db.SaveChangesAsync(ct);

        if (capacity is not null)
            foreach (var agentId in agents)
                await capacity.InvalidateAsync(lead.TenantId, agentId, ct);

        return Result.Ok();
    }
}
