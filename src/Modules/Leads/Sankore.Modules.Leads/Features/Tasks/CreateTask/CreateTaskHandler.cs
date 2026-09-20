namespace Sankore.Modules.Leads.Features.Tasks.CreateTask;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateTaskHandler(LeadsDbContext db, ITenantContext tenantContext)
    : IRequestHandler<CreateTaskCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateTaskCommand cmd, CancellationToken ct)
    {
        var task = CrmTask.Create(
            tenantId:        tenantContext.CurrentTenantId,
            type:            cmd.Type,
            priority:        cmd.Priority,
            title:           cmd.Title,
            dueAt:           cmd.DueAt,
            leadId:          cmd.LeadId,
            assignedAgentId: cmd.AssignedAgentId,
            slaDeadline:     cmd.SlaDeadline,
            description:     cmd.Description);

        db.CrmTasks.Add(task);
        await db.SaveChangesAsync(ct);

        return Result.Ok(task.Id);
    }
}
