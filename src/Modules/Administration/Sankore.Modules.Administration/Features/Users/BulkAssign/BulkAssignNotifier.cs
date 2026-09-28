using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;

namespace Sankore.Modules.Administration.Features.Users.BulkAssign;

/// <summary>
/// Tells a user their account changed under a bulk action: a new home agency, or a new role.
///
/// One message per user who was ACTUALLY changed — the skipped rows of a partial batch get
/// nothing, which is also what makes a replayed request harmless. The second time round those
/// users are already in the agency, or already hold the role, so they are skipped and no second
/// mail is produced; the silence comes from the skip, not from extra de-duplication.
/// </summary>
internal sealed class BulkAssignNotifier(ModuleEmailSender mail)
{
    public const string AgencyChangedTemplate = "user.agency-changed";
    public const string RoleGrantedTemplate = "user.role-granted";

    public Task NotifyMovedAsync(
        Agency agency,
        AppUser user,
        DateTimeOffset changedAt,
        CancellationToken ct)
        => mail.SendAsync(
            agency.TenantId,
            user,
            AgencyChangedTemplate,
            // changedAt is stamped once per command, so every genuine change is a distinct
            // message even when someone is moved back and forth between two agencies.
            idempotencyKey: $"{AgencyChangedTemplate}-{user.Id}-{agency.Id}-{changedAt:O}",
            extra: new Dictionary<string, object>
            {
                ["agency_name"] = agency.Name,
                ["agency_code"] = agency.Code,
            },
            ct);

    public Task NotifyRoleGrantedAsync(
        Guid tenantId,
        AppRole role,
        AppUser user,
        DateTimeOffset changedAt,
        CancellationToken ct)
        => mail.SendAsync(
            tenantId,
            user,
            RoleGrantedTemplate,
            idempotencyKey: $"{RoleGrantedTemplate}-{user.Id}-{role.Id}-{changedAt:O}",
            extra: new Dictionary<string, object>
            {
                // Label is what a human recognises; Name is the code the policies use.
                ["role_name"] = string.IsNullOrWhiteSpace(role.Label) ? role.Name ?? string.Empty : role.Label,
            },
            ct);
}
