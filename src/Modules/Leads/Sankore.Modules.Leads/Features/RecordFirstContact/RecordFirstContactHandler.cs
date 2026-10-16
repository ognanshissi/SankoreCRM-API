namespace Sankore.Modules.Leads.Features.RecordFirstContact;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class RecordFirstContactHandler(LeadsDbContext db)
    : IRequestHandler<RecordFirstContactCommand, Result>
{
    public async Task<Result> Handle(
        RecordFirstContactCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        if (lead.CurrentAssignmentId is null)
            return Result.Fail("LEAD_HAS_NO_ACTIVE_ASSIGNMENT");

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
        // No db.Leads.Update(lead): the lead is already tracked, and calling Update marks the
        // OWNED PrincipalPep's identifying key as modified — EF then refuses the save with
        // "part of a key and so cannot be modified".
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
