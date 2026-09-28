namespace Sankore.Modules.Customers.Features.Groups.SuspendGroup;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class SuspendGroupHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope
) : IRequestHandler<SuspendGroupCommand, Result<SuspendGroupResult>>
{
    public async Task<Result<SuspendGroupResult>> Handle(SuspendGroupCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Result.Fail<SuspendGroupResult>(CustomerErrors.ReasonRequired);

        var group = await db.ClientGroups
            .AsTracking()
            .Include(g => g.Memberships)
            .FirstOrDefaultAsync(g => g.Id == request.GroupId, ct);

        if (group is null)
            return Result.Fail<SuspendGroupResult>(CustomerErrors.GroupNotFound);

        if (!await GroupAccess.CanAccessAsync(agencyScope, currentUser.TenantId, currentUser.Id, group, ct))
            return Result.Fail<SuspendGroupResult>(CustomerErrors.GroupNotFound);

        if (request.ExpectedVersion.HasValue && group.Version != request.ExpectedVersion.Value)
            return Result.Fail<SuspendGroupResult>(CustomerErrors.ConcurrencyConflict);

        var suspension = group.Suspend(request.Reason.Trim(), currentUser.Id);

        if (suspension.IsFailure)
            return Result.Fail<SuspendGroupResult>(suspension.Error!);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail<SuspendGroupResult>(CustomerErrors.ConcurrencyConflict);
        }

        return Result.Ok(new SuspendGroupResult(
            group.Id, group.Status.ToString(), group.ActiveMemberCount));
    }
}
