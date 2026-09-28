namespace Sankore.Modules.Customers.Features.Duplicates.Merge.ApproveClientMerge;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Approves a merge request and runs it.
///
/// <para><b>Why the four-eyes rule lives here and not in M12.</b> A workflow instance is started
/// when the request is opened, and it is what puts the approval on someone's task list — but the
/// M12 engine imposes no self-approval control: whoever can act on a step can act on their own
/// step. Merging two clients is irreversible, so the rule has to be enforced where it cannot be
/// configured away: this handler refuses <c>RequestedBy == approver</c> outright
/// (<see cref="CustomerErrors.SelfApprovalForbidden"/>), and the aggregate refuses it a second time
/// in <see cref="ClientMergeRequest.Approve"/>. That is also why the request survives a missing
/// workflow template — an unconfigured M12 must not silently disable the control.</para>
///
/// <para>Hooking the execution onto the completion of an M12 instance instead (a
/// <c>WorkflowCompleted</c> consumer) would first require M12 to publish that event from its
/// PublicApi: the event type currently lives in its main assembly, which no other module may
/// reference. Out of scope for this US — deliberately no consumer here.</para>
/// </summary>
internal sealed class ApproveClientMergeHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IClientMergeExecutor executor,
    TimeProvider clock)
    : IRequestHandler<ApproveClientMergeCommand, Result>
{
    public async Task<Result> Handle(ApproveClientMergeCommand command, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        var request = await db.ClientMergeRequests
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == command.MergeRequestId, ct);

        if (request is null)
            return Result.Fail(CustomerErrors.MergeRequestNotFound);

        // Checked before the aggregate's own guard so the caller gets the precise code even when the
        // request has already been decided by someone else in the meantime.
        if (request.RequestedBy == currentUser.Id)
            return Result.Fail(CustomerErrors.SelfApprovalForbidden);

        if (request.Status != MergeRequestStatus.PendingApproval)
            return Result.Fail(CustomerErrors.MergeAlreadyDecided);

        if (request.SurvivorClientId == request.AbsorbedClientId)
            return Result.Fail(CustomerErrors.SameClientMergeForbidden);

        var scoped = await IsInsidePerimeterAsync(request, tenantId, ct);
        if (scoped.IsFailure)
            return scoped;

        var approved = request.Approve(currentUser.Id, command.Comment, clock.GetUtcNow());
        if (approved.IsFailure)
            return approved;

        var executed = await executor.ExecuteAsync(request, currentUser.Id, ct);
        if (executed.IsFailure)
            return executed;

        // One save for the approval, the reassignments, the absorbed client's new status and the
        // outbox rows the executor queued — TransactionBehavior wraps it all.
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    /// <summary>
    /// Both clients of the merge must be inside the approver's agency perimeter; out of perimeter
    /// answers CLIENT_NOT_FOUND so the endpoint replies 404 and reveals nothing.
    /// </summary>
    private async Task<Result> IsInsidePerimeterAsync(
        ClientMergeRequest request, Guid tenantId, CancellationToken ct)
    {
        var agencyIds = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && (c.Id == request.SurvivorClientId || c.Id == request.AbsorbedClientId))
            .Select(c => c.AgencyId)
            .ToListAsync(ct);

        if (agencyIds.Count < 2)
            return Result.Fail(CustomerErrors.ClientNotFound);

        foreach (var agencyId in agencyIds)
        {
            if (!await agencyScope.CanAccessAgencyAsync(tenantId, currentUser.Id, agencyId, ct))
                return Result.Fail(CustomerErrors.ClientNotFound);
        }

        return Result.Ok();
    }
}
