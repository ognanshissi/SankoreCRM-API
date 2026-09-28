using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AssignManager;

internal sealed class AssignManagerHandler(
    AdministrationDbContext db,
    ReportingLine reportingLine
) : IRequestHandler<AssignManagerCommand, Result<AssignManagerResult>>
{
    public async Task<Result<AssignManagerResult>> Handle(
        AssignManagerCommand request, CancellationToken ct)
    {
        if (request.UserId == request.ManagerUserId)
            return Result.Fail<AssignManagerResult>(AssignManagerErrors.SelfReportForbidden);

        // The tenant query filter applies, so a user of another tenant simply does not exist.
        var user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null)
            return Result.Fail<AssignManagerResult>(AssignManagerErrors.UserNotFound);

        if (user.AccountType == UserAccountType.System)
            return Result.Fail<AssignManagerResult>(AssignManagerErrors.SystemAccountImmutable);

        var manager = await db.Users
            .Select(u => new { u.Id, u.FullName, u.Status, u.AccountType })
            .FirstOrDefaultAsync(u => u.Id == request.ManagerUserId, ct);

        if (manager is null)
            return Result.Fail<AssignManagerResult>(AssignManagerErrors.ManagerNotFound);

        // The system account is a technical identity for background work, not a line manager.
        if (manager.AccountType == UserAccountType.System)
            return Result.Fail<AssignManagerResult>(AssignManagerErrors.SystemAccountImmutable);

        if (manager.Status != UserStatus.Active)
            return Result.Fail<AssignManagerResult>(AssignManagerErrors.ManagerNotActive);

        var previous = user.ReportsToUserId;

        // Nothing to check when the line is unchanged — and nothing to write either.
        if (previous != manager.Id)
        {
            if (await reportingLine.WouldCreateCycleAsync(user.Id, manager.Id, ct))
                return Result.Fail<AssignManagerResult>(AssignManagerErrors.ReportingCycle);

            user.ReportTo(manager.Id);
            await db.SaveChangesAsync(ct);
        }

        return Result.Ok(new AssignManagerResult(
            UserId: user.Id,
            ManagerUserId: manager.Id,
            ManagerFullName: manager.FullName,
            PreviousManagerUserId: previous,
            Changed: previous != manager.Id));
    }
}

internal sealed class ClearManagerHandler(
    AdministrationDbContext db
) : IRequestHandler<ClearManagerCommand, Result>
{
    public async Task<Result> Handle(ClearManagerCommand request, CancellationToken ct)
    {
        var user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null)
            return Result.Fail(AssignManagerErrors.UserNotFound);

        // Whoever reported to this user keeps reporting to them: clearing someone's own manager
        // says nothing about their subordinates, and silently re-parenting a whole subtree is
        // exactly the kind of side effect nobody asks for.
        user.ClearReportingLine();
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
