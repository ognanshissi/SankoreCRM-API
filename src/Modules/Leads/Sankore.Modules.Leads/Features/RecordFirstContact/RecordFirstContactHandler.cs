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
        assignment.RecordFirstContact(contactedAt);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
