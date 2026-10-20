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
