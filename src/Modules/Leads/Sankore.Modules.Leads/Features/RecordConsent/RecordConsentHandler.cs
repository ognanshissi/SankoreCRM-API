namespace Sankore.Modules.Leads.Features.RecordConsent;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class RecordConsentHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<RecordConsentCommand, Result<RecordConsentResult>>
{
    internal const string ActivityNotFound = "LEAD_ACTIVITY_NOT_FOUND";
    internal const string WithdrawFirst = "CONSENT_ACTIVE_WITHDRAW_FIRST";

    public async Task<Result<RecordConsentResult>> Handle(
        RecordConsentCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<RecordConsentResult>("LEAD_NOT_FOUND");

        // A proof that belongs to another lead is worse than none: it would show somebody else's
        // call as the evidence of this prospect's answer.
        if (cmd.ProofActivityId is { } activityId
            && !await db.LeadActivities.AnyAsync(a => a.Id == activityId && a.LeadId == lead.Id, ct))
            return Result.Fail<RecordConsentResult>(ActivityNotFound);

        // A refusal while an agreement stands would leave the agreement ACTIVE — every check reads
        // the active row, not the latest one. Changing one's mind is a withdrawal, which is tracked
        // as such; the screen offers « Retirer » on an active consent for exactly this reason.
        if (!cmd.Granted
            && await db.LeadConsents.AnyAsync(c => c.LeadId == lead.Id && c.Type == cmd.Type
                                                 && c.SubjectId == null
                                                 && c.Status == ConsentStatus.Active, ct))
            return Result.Fail<RecordConsentResult>(WithdrawFirst);

        // WhatsApp (WA-BE-05, WA-FE-03): an agent typing an opt-in says why — it is the only
        // evidence Meta's policy can be shown — and after a withdrawal, a new agreement needs a
        // NEW proof: the logged call or visit in which the customer changed their mind. Without it
        // a click would undo an opt-out, which is the one thing a customer must be able to rely on.
        if (cmd.Granted && cmd.Type == ConsentType.WhatsAppContact)
        {
            if (string.IsNullOrWhiteSpace(cmd.ProofReference))
                return Result.Fail<RecordConsentResult>(Features.WhatsApp.WhatsAppErrors.ConsentReasonRequired);

            var rows = await db.LeadConsents
                .Where(c => c.LeadId == lead.Id && c.Type == ConsentType.WhatsAppContact && c.SubjectId == null)
                .ToListAsync(ct);

            var revoked = Features.WhatsApp.WhatsAppEligibility.ConsentStateOf(rows)
                          == Features.WhatsApp.WhatsAppConsentState.Revoked;

            if (revoked && cmd.ProofActivityId is null)
                return Result.Fail<RecordConsentResult>(Features.WhatsApp.WhatsAppErrors.ConsentNewProofRequired);
        }

        var consent = LeadConsent.Create(
            tenantId:       cmd.TenantId,
            leadId:         cmd.LeadId,
            type:           cmd.Type,
            channel:        cmd.Channel,
            recordedBy:     cmd.RecordedBy,
            clock:          clock,
            proofReference: cmd.ProofReference,
            granted:        cmd.Granted,
            proofActivityId: cmd.ProofActivityId);

        db.LeadConsents.Add(consent);

        // Only an agreement is announced as a recorded consent: the event's consumers read it as
        // « this prospect may now be contacted / processed », which a refusal is the opposite of.
        if (cmd.Granted)
            lead.RaiseConsentRecordedEvent(consent.Id, cmd.Type.ToString());

        await db.SaveChangesAsync(ct);

        return Result.Ok(new RecordConsentResult(consent.Id, consent.GrantedAt));
    }
}
