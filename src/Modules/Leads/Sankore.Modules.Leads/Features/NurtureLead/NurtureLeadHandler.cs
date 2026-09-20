namespace Sankore.Modules.Leads.Features.NurtureLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class NurtureLeadHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<NurtureLeadCommand, Result>
{
    public async Task<Result> Handle(NurtureLeadCommand cmd, CancellationToken ct)
    {
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

        var result = lead.Nurture();
        if (result.IsFailure)
            return result;

        // ── Enroll into sequence if provided ────────────────────────────
        if (cmd.SequenceId.HasValue)
        {
            var sequence = await db.NurturingSequences
                .Include(s => s.Steps)
                .FirstOrDefaultAsync(s => s.Id == cmd.SequenceId.Value && s.IsActive, ct);

            if (sequence is null)
                return Result.Fail("SEQUENCE_NOT_FOUND");

            if (sequence.Steps.Count == 0)
                return Result.Fail("SEQUENCE_HAS_NO_STEPS");

            // Check not already enrolled in an active sequence
            var alreadyEnrolled = await db.NurturingEnrollments
                .AnyAsync(e => e.LeadId == cmd.LeadId
                            && e.Status == NurturingEnrollmentStatus.Active, ct);

            if (alreadyEnrolled)
                return Result.Fail("LEAD_ALREADY_ENROLLED");

            var firstStep = sequence.Steps.OrderBy(s => s.Order).First();
            var enrollment = NurturingEnrollment.Create(
                tenantId:       lead.TenantId,
                leadId:         lead.Id,
                sequenceId:     sequence.Id,
                firstStepDelay: firstStep.DelayFromPrevious,
                clock:          clock);

            db.NurturingEnrollments.Add(enrollment);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
