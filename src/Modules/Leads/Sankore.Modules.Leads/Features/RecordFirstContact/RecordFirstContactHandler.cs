namespace Sankore.Modules.Leads.Features.RecordFirstContact;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class RecordFirstContactHandler(LeadsDbContext db, LeadAccessGuard accessGuard)
    : IRequestHandler<RecordFirstContactCommand, Result>
{
    public async Task<Result> Handle(
        RecordFirstContactCommand cmd, CancellationToken ct)
    {
        // AsTracking: this context does not track by default, and the lead now changes too.
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        // The endpoint is open to agents (lead:activity:log): only the lead's holder, its owner or
        // their supervisor may say it was contacted.
        var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
        if (access.IsFailure)
            return access;

        // A lead its agent typed in is owned, never dispatched (CaptureLeadEndpoint.ResolveOwner):
        // no assignment, so no SLA to stop — the contact still moves the lead to « Contacté ».
        if (lead.CurrentAssignmentId is null)
        {
            if (lead.OwnerId is null)
                return Result.Fail("LEAD_HAS_NO_ACTIVE_ASSIGNMENT");
            if (!lead.RecordContactEstablished())
                return Result.Fail("FIRST_CONTACT_ALREADY_RECORDED");
            await db.SaveChangesAsync(ct);
            return Result.Ok();
        }

        var assignment = await db.LeadAssignments
            .AsTracking()
            .FirstOrDefaultAsync(a => a.Id == lead.CurrentAssignmentId, ct);

        if (assignment is null)
            return Result.Fail("ASSIGNMENT_NOT_FOUND");

        if (assignment.FirstContactAt.HasValue)
            return Result.Fail("FIRST_CONTACT_ALREADY_RECORDED");

        var contactedAt = cmd.ContactedAt ?? DateTimeOffset.UtcNow;

        // AgentDeclared: this endpoint IS an agent pressing "I called them". The telephony path
        // records TelephonyCall, and a manager reading a contact rate can then tell a claim from
        // evidence — see FirstContactSource.
        assignment.RecordFirstContact(contactedAt, Domain.FirstContactSource.AgentDeclared);
        // The assignment's first-contact task is done: it disappears from "Mes tâches" instead of
        // waiting there, overdue, for a call that already happened.
        await Tasks.AssignmentTasks.CompleteFirstContactAsync(db, lead.TenantId, lead.Id, assignment.AgentId, ct);
        // And the lead itself is now contacted: "Contacté", stage "Contact établi".
        lead.RecordContactEstablished();
        // No db.Leads.Update(lead): the lead is already tracked, and calling Update marks the
        // OWNED PrincipalPep's identifying key as modified — EF then refuses the save with
        // "part of a key and so cannot be modified".
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
