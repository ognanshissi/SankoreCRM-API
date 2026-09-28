namespace Sankore.Modules.Customers.Features.Duplicates.Merge.RejectClientMerge;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Rejects a pending merge request. Same four-eyes rule as the approval: the requester cannot close
/// their own request, in either direction. The aggregate enforces the self-approval rule on
/// <c>Approve</c> only, so the symmetric check on rejection is made here — letting a requester
/// cancel their own request would be a different feature (a withdrawal), with its own permission.
/// </summary>
internal sealed class RejectClientMergeHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    TimeProvider clock)
    : IRequestHandler<RejectClientMergeCommand, Result>
{
    public async Task<Result> Handle(RejectClientMergeCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
            return Result.Fail(CustomerErrors.ReasonRequired);

        var tenantId = currentUser.TenantId;

        var request = await db.ClientMergeRequests
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == command.MergeRequestId, ct);

        if (request is null)
            return Result.Fail(CustomerErrors.MergeRequestNotFound);

        if (request.RequestedBy == currentUser.Id)
            return Result.Fail(CustomerErrors.SelfApprovalForbidden);

        if (request.Status != MergeRequestStatus.PendingApproval)
            return Result.Fail(CustomerErrors.MergeAlreadyDecided);

        // Both clients must be inside the perimeter, exactly as for an approval: deciding a merge
        // request is a decision about two records.
        var agencyIds = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && (c.Id == request.SurvivorClientId || c.Id == request.AbsorbedClientId))
            .Select(c => c.AgencyId)
            .ToListAsync(ct);

        foreach (var agencyId in agencyIds)
        {
            if (!await agencyScope.CanAccessAgencyAsync(tenantId, currentUser.Id, agencyId, ct))
                return Result.Fail(CustomerErrors.ClientNotFound);
        }

        var rejected = request.Reject(currentUser.Id, command.Reason, clock.GetUtcNow());
        if (rejected.IsFailure)
            return rejected;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
