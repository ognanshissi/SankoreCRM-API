using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;

/// <summary>
/// Tells the people affected when an agency changes hands: the incoming manager that they now
/// run it, and the outgoing one that they no longer do.
///
/// Shared by the assign and remove handlers because a replacement has to do both, and the two
/// messages must agree on locale resolution and on how idempotency keys are built.
///
/// Every method swallows its own failures. Queuing a mail is a side effect of an administrative
/// decision that has already been taken and committed; a mail server being down must not undo it.
/// </summary>
internal sealed class AgencyManagerNotifier(
    AdministrationDbContext db,
    ModuleEmailSender mail,
    ILogger<AgencyManagerNotifier> logger)
{
    public const string AssignedTemplate = "agency.manager-assigned";
    public const string UnassignedTemplate = "agency.manager-unassigned";

    /// <summary>The newcomer learns which agency they now run and which role they gained.</summary>
    public Task NotifyAssignedAsync(Agency agency, AppUser manager, CancellationToken ct)
        => mail.SendAsync(
            agency.TenantId,
            manager,
            AssignedTemplate,
            IdempotencyKey(AssignedTemplate, agency, manager.Id),
            AgencyData(agency, new Dictionary<string, object>
            {
                ["role_name"] = global::Sankore.Shared.Kernel.Roles.BranchManager.Code,
            }),
            ct);

    /// <summary>
    /// The outgoing manager learns the responsibility has moved on. Deliberately says nothing
    /// about who replaced them — that is the administrator's news to break, not a system mail's —
    /// and nothing about the role, which they may well keep if they still run another agency.
    /// </summary>
    public async Task NotifyUnassignedAsync(Agency agency, Guid outgoingManagerId, CancellationToken ct)
    {
        var outgoing = await db.Users.FirstOrDefaultAsync(u => u.Id == outgoingManagerId, ct);
        if (outgoing is null)
        {
            logger.LogWarning(
                "Outgoing manager {ManagerId} of agency {AgencyId} no longer exists; notification skipped.",
                outgoingManagerId, agency.Id);
            return;
        }

        await mail.SendAsync(
            agency.TenantId,
            outgoing,
            UnassignedTemplate,
            IdempotencyKey(UnassignedTemplate, agency, outgoing.Id),
            AgencyData(agency, extra: null),
            ct);
    }

    private static Dictionary<string, object> AgencyData(Agency agency, Dictionary<string, object>? extra)
    {
        var data = new Dictionary<string, object>
        {
            ["agency_name"] = agency.Name,
            ["agency_code"] = agency.Code,
        };

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
                data[key] = value;
        }

        return data;
    }

    /// <summary>
    /// The agency's UpdatedAt stamp is what makes a later, genuine change of manager a distinct
    /// message instead of one the outbox discards as a duplicate; the template name keeps the two
    /// halves of a handover from colliding with each other.
    /// </summary>
    private static string IdempotencyKey(string templateKey, Agency agency, Guid recipientId)
        => $"{templateKey}-{agency.Id}-{recipientId}-{agency.UpdatedAt:O}";
}
