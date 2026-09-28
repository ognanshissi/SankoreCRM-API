using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.BulkAssign;

internal sealed class BulkAssignAgencyHandler(
    AdministrationDbContext db,
    BulkAssignNotifier notifier
) : IRequestHandler<BulkAssignAgencyCommand, Result<BulkAssignResult>>
{
    public async Task<Result<BulkAssignResult>> Handle(
        BulkAssignAgencyCommand request, CancellationToken ct)
    {
        var agency = await db.Agencies.FirstOrDefaultAsync(a => a.Id == request.AgencyId, ct);
        if (agency is null)
            return Result.Fail<BulkAssignResult>(BulkAssignErrors.AgencyNotFound);
        if (agency.IsDeleted)
            return Result.Fail<BulkAssignResult>(BulkAssignErrors.AgencyDeleted);

        // The same user selected twice is one move, not two.
        var userIds = request.UserIds.Distinct().ToList();

        var users = await db.Users
            .AsTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(ct);

        // Which of them currently run an agency: moving such a user elsewhere would leave an
        // agency with a manager who no longer belongs to it.
        var managedElsewhere = await db.Agencies
            .Where(a => !a.IsDeleted && a.ManagerUserId != null && a.Id != request.AgencyId)
            .Select(a => a.ManagerUserId!.Value)
            .ToListAsync(ct);
        var managers = managedElsewhere.ToHashSet();

        var outcomes = new List<BulkUserOutcome>(userIds.Count);
        var moved = new List<AppUser>();

        foreach (var userId in userIds)
        {
            var user = users.Find(u => u.Id == userId);

            if (user is null)
            {
                outcomes.Add(new(userId, false, BulkAssignReasons.UserNotFound));
                continue;
            }

            if (user.AccountType == UserAccountType.System)
            {
                outcomes.Add(new(userId, false, BulkAssignReasons.SystemAccountImmutable));
                continue;
            }

            if (user.AgencyId == request.AgencyId)
            {
                outcomes.Add(new(userId, false, BulkAssignReasons.AlreadyInAgency));
                continue;
            }

            if (managers.Contains(userId))
            {
                outcomes.Add(new(userId, false, BulkAssignReasons.UserManagesAnAgency));
                continue;
            }

            user.UpdateDetails(
                fullName: null,
                agencyId: request.AgencyId,
                spokenLanguages: null,
                specialties: null,
                enableNotifications: null);

            outcomes.Add(new(userId, true, null));
            moved.Add(user);
        }

        await db.SaveChangesAsync(ct);

        // Only the users who actually moved are told, and only once the move is committed.
        var changedAt = DateTimeOffset.UtcNow;
        foreach (var user in moved)
            await notifier.NotifyMovedAsync(agency, user, changedAt, ct);

        return Result.Ok(Report(userIds.Count, outcomes));
    }

    internal static BulkAssignResult Report(int requested, List<BulkUserOutcome> outcomes)
    {
        var applied = outcomes.Count(o => o.Applied);
        return new BulkAssignResult(requested, applied, outcomes.Count - applied, outcomes);
    }
}
