namespace Sankore.Modules.Leads.Features.RecordConsent;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class RecordConsentHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<RecordConsentCommand, Result<RecordConsentResult>>
{
    public async Task<Result<RecordConsentResult>> Handle(
        RecordConsentCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<RecordConsentResult>("LEAD_NOT_FOUND");

        var consent = LeadConsent.Create(
            tenantId:       cmd.TenantId,
            leadId:         cmd.LeadId,
            type:           cmd.Type,
            channel:        cmd.Channel,
            recordedBy:     cmd.RecordedBy,
            clock:          clock,
            proofReference: cmd.ProofReference);

        db.LeadConsents.Add(consent);

        lead.RaiseConsentRecordedEvent(consent.Id, cmd.Type.ToString());

        await db.SaveChangesAsync(ct);

        return Result.Ok(new RecordConsentResult(consent.Id, consent.GrantedAt));
    }
}
