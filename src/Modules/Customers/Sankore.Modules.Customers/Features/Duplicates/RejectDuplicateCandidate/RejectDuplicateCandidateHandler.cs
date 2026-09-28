namespace Sankore.Modules.Customers.Features.Duplicates.RejectDuplicateCandidate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Duplicates;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Marks a candidate pair as rejected. The nightly detection then leaves it alone for as long as the
/// compared data is unchanged — see the fingerprint rule in <c>DetectDuplicatesHandler</c>.
/// </summary>
internal sealed class RejectDuplicateCandidateHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    TimeProvider clock)
    : IRequestHandler<RejectDuplicateCandidateCommand, Result>
{
    public async Task<Result> Handle(RejectDuplicateCandidateCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
            return Result.Fail(CustomerErrors.ReasonRequired);

        var tenantId = currentUser.TenantId;

        var candidate = await db.DuplicateCandidates
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == command.CandidateId, ct);

        if (candidate is null)
            return Result.Fail(DuplicatesErrors.DuplicateCandidateNotFound);

        if (candidate.Status == DuplicateCandidateStatus.Merged)
            return Result.Fail(CustomerErrors.ClientAlreadyMerged);

        // Perimeter is checked on BOTH clients: rejecting a pair is a decision about two records,
        // so seeing only one of them is not enough. Out of perimeter answers "not found" rather
        // than "forbidden", exactly like a single client read, so the row's existence stays hidden.
        var agencyIds = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && (c.Id == candidate.ClientAId || c.Id == candidate.ClientBId))
            .Select(c => c.AgencyId)
            .ToListAsync(ct);

        foreach (var agencyId in agencyIds)
        {
            if (!await agencyScope.CanAccessAgencyAsync(tenantId, currentUser.Id, agencyId, ct))
                return Result.Fail(CustomerErrors.ClientNotFound);
        }

        candidate.Reject(currentUser.Id, clock.GetUtcNow());

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
