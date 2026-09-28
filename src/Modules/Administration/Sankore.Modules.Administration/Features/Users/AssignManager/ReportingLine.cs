using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;

namespace Sankore.Modules.Administration.Features.Users.AssignManager;

/// <summary>
/// Answers the one question a reporting line cannot answer locally: would this assignment close
/// a loop?
/// </summary>
internal sealed class ReportingLine(AdministrationDbContext db)
{
    /// <summary>
    /// Guards against a hierarchy that is already corrupt — a cycle written before this check
    /// existed, or straight into the database — so the walk terminates whatever it finds.
    /// Also a sane ceiling: no real organisation is 100 levels deep.
    /// </summary>
    private const int MaxDepth = 100;

    /// <summary>
    /// True when <paramref name="managerUserId"/> already reports, directly or through any number
    /// of intermediaries, to <paramref name="userId"/>. Walking UP from the proposed manager is
    /// what makes this cheap: a line has one parent per node, so this is at most MaxDepth
    /// single-row lookups, not a traversal of the whole tree.
    /// </summary>
    public async Task<bool> WouldCreateCycleAsync(Guid userId, Guid managerUserId, CancellationToken ct)
    {
        var current = managerUserId;
        var seen = new HashSet<Guid> { managerUserId };

        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var parent = await db.Users
                .Where(u => u.Id == current)
                .Select(u => u.ReportsToUserId)
                .FirstOrDefaultAsync(ct);

            if (parent is not { } next)
                return false;           // reached the top: no loop

            if (next == userId)
                return true;            // the proposed manager already reports to this user

            if (!seen.Add(next))
                return true;            // pre-existing loop upstream; refuse to add to it

            current = next;
        }

        // Deeper than any plausible organisation: treat as a loop rather than assume it is fine.
        return true;
    }
}
