namespace Sankore.Modules.Leads.Features.WithdrawConsent;

using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.NurturingExecution;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class WithdrawConsentHandler(LeadsDbContext db, TimeProvider clock, IBus bus)
    : IRequestHandler<WithdrawConsentCommand, Result>
{
    public async Task<Result> Handle(WithdrawConsentCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        var consent = await db.LeadConsents
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ConsentId && c.LeadId == cmd.LeadId, ct);

        if (consent is null)
            return Result.Fail("CONSENT_NOT_FOUND");

        var withdrawResult = consent.Withdraw(cmd.WithdrawnBy, clock, cmd.Reason);
        if (withdrawResult.IsFailure)
            return withdrawResult;

        lead.RaiseConsentWithdrawnEvent(consent.Id, consent.Type.ToString());

        await db.SaveChangesAsync(ct);

        // Immediately cancel nurturing enrollments if marketing consent is withdrawn (US-M13-151)
        if (consent.Type == ConsentType.Marketing)
        {
            await bus.Publish(new MarketingConsentWithdrawnEvent(
                lead.TenantId, lead.Id), ct);
        }

        return Result.Ok();
    }
}
