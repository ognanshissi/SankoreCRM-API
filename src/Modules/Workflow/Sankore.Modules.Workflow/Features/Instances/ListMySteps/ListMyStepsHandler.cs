using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.ListMySteps;

internal sealed class ListMyStepsHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser,
    Infrastructure.Gates.SigningGuard? signingGuard = null
) : IRequestHandler<ListMyStepsQuery, Result<IReadOnlyList<MyStepDto>>>
{
    public async Task<Result<IReadOnlyList<MyStepDto>>> Handle(
        ListMyStepsQuery request, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var roles  = currentUser.Roles;

        // A step is "mine" when:
        //   a) Explicitly assigned to me, OR
        //   b) Not assigned to anyone AND my role matches ApproverRoleCode.
        var steps = await db.WorkflowInstanceSteps
            .Where(s => s.Status == StepStatus.AwaitingApproval
                     && (s.AssignedToUserId == userId
                         || (s.AssignedToUserId == null
                             && s.ApproverRoleCode != null
                             && roles.Contains(s.ApproverRoleCode))))
            .OrderBy(s => s.DueAt)
            .Join(db.WorkflowInstances, s => s.InstanceId, i => i.Id, (s, i) => new { s, i })
            // Cancelling an instance leaves its open step « en attente »: it stayed in the queue,
            // approvable in appearance, for a circuit nobody runs any more (found 2026-11-02).
            .Where(x => x.i.Status == WorkflowStatus.InProgress)
            .Select(x => new MyStepDto(
                x.s.Id,
                x.s.InstanceId,
                x.s.Name,
                x.s.ApproverRoleCode,
                x.s.AssignedToUserId,
                x.s.DueAt,
                x.s.CreatedAt,
                x.i.EntityType,
                x.i.EntityId,
                db.WorkflowTemplates.Where(t => t.Id == x.i.TemplateId).Select(t => t.Name).FirstOrDefault(),
                x.i.SubjectLabel,
                x.i.SubjectDetail,
                x.i.SubjectLink))
            .ToListAsync(ct);

        if (signingGuard is null) return Result.Ok<IReadOnlyList<MyStepDto>>(steps);

        // A step reaching me through my role, that I could only be refused on, is not mine to do.
        // One handed to me by name stays: I have to see it to say it must go to someone else.
        var refusals = await signingGuard.RefusalsAsync(
            currentUser.TenantId,
            steps.Where(s => s.AssignedToUserId is null)
                .Select(s => new Infrastructure.Gates.SigningGuard.Candidate(s.InstanceId, s.EntityType!, s.EntityId!.Value))
                .ToList(),
            ct);

        return Result.Ok<IReadOnlyList<MyStepDto>>(
            steps.Where(s => s.AssignedToUserId is not null || !refusals.ContainsKey(s.InstanceId)).ToList());
    }
}
