namespace Sankore.Modules.Leads.Features.RecycleLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Tasks;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class RecycleLeadHandler(LeadsDbContext db, TimeProvider clock, AgentCapacityService? capacity = null)
    : IRequestHandler<RecycleLeadCommand, Result>
{
    public async Task<Result> Handle(RecycleLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        var now = clock.GetUtcNow();
        if (cmd.ReactivateOn is { } day && day <= DateOnly.FromDateTime(now.UtcDateTime))
            return Result.Fail("REACTIVATION_DATE_NOT_IN_FUTURE");

        var result = lead.Recycle(cmd.NewSource, cmd.NewCampaign, cmd.ReactivateOn);
        if (result.IsFailure)
            return result;

        var agents = await AssignmentTasks.SetAsideAsync(db, lead, now, ct);

        // The screen promises it: whoever holds the lead finds it again on the chosen day, with the
        // reason it was set aside. Nobody holds it: the managers see the task in the task list.
        if (cmd.ReactivateOn is { } dueDay)
            db.CrmTasks.Add(CrmTask.Create(
                tenantId:        lead.TenantId,
                type:            CrmTaskType.Generic,
                priority:        CrmTaskPriority.Medium,
                title:           $"Reprendre le lead — {lead.FullName}",
                dueAt:           new DateTimeOffset(dueDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                leadId:          lead.Id,
                assignedAgentId: lead.CurrentAssignedId ?? lead.OwnerId,
                description:     string.IsNullOrWhiteSpace(cmd.Reason) ? null : $"Motif du recyclage : {cmd.Reason.Trim()}"));

        // No db.Leads.Update(lead): the lead is already tracked, and calling Update marks the
        // OWNED PrincipalPep's identifying key as modified — EF then refuses the save with
        // "part of a key and so cannot be modified".
        await db.SaveChangesAsync(ct);

        if (capacity is not null)
            foreach (var agentId in agents)
                await capacity.InvalidateAsync(lead.TenantId, agentId, ct);

        return Result.Ok();
    }
}
