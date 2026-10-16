namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates the link, or revives the one that was switched off (TEL-B-05).
///
/// <para>
/// <b>Reviving rather than inserting a second row is what makes the unique indexes livable.</b>
/// Unlinking is logical — the row stays, because an agent's past calls point at it — so an
/// administrator who unlinks somebody and links them again would otherwise hit
/// <c>ux_tel_user_link_crm_user</c> and read "already taken" about a link the screen shows as
/// gone.
/// </para>
/// </summary>
internal sealed class LinkTelephonyUserHandler(
    IntegrationDbContext db,
    TelephonyAccess access,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<LinkTelephonyUserHandler> logger)
    : IRequestHandler<LinkTelephonyUserCommand, Result<Guid>>
{
    private const string CrmUserIndex = "ux_tel_user_link_crm_user";
    private const string ExternalUserIndex = "ux_tel_user_link_external_user";

    public async Task<Result<Guid>> Handle(LinkTelephonyUserCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // The connection, not the port: linking writes a row of this module and must keep
        // working while the provider is unreachable. An administrator told "come back when your
        // provider answers" cannot repair a link that broke BECAUSE the provider changed.
        var connection = await access.ResolveAsync(currentUser.TenantId, ct);

        if (connection.IsFailure)
            return Result.Fail<Guid>(connection.Code!);

        var connectionId = connection.Value.Connection.Id;
        var externalUserId = cmd.ExternalUserId.Trim();
        var now = clock.GetUtcNow();

        var existing = await db.TelUserLinks
            .AsTracking()
            .Where(l => l.ConnectionId == connectionId
                     && (l.CrmUserId == cmd.CrmUserId || l.ExternalUserId == externalUserId))
            .ToListAsync(ct);

        // A live link on either side that is not the exact pair being asked for: refused, and the
        // caller is told which side. Silently moving an account from one user to another would
        // re-attribute every call that follows with nothing in the audit trail saying so.
        if (existing.FirstOrDefault(l =>
                l.Status != TelUserLinkStatus.Disabled
                && (l.CrmUserId != cmd.CrmUserId || l.ExternalUserId != externalUserId)) is not null)
        {
            return Result.Fail<Guid>(TelephonyErrors.UserLinkAlreadyTaken);
        }

        var exact = existing.FirstOrDefault(
            l => l.CrmUserId == cmd.CrmUserId && l.ExternalUserId == externalUserId);

        if (exact is not null)
        {
            // Already linked, exactly as asked: a success and not a conflict — a double-clicked
            // confirm button must not read as the uniqueness rule firing. A broken or disabled
            // row is revived, which is the repair path the nightly check creates work for.
            var revived = exact.Relink(currentUser.Id, now, cmd.ExternalEmail);

            if (cmd.DefaultCallerNumber is not null)
                exact.SetDefaultCallerNumber(cmd.DefaultCallerNumber);

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Telephony link {LinkId} for CRM user {CrmUserId} on connection {ConnectionId} "
                + "confirmed ({Action}).",
                exact.Id, exact.CrmUserId, connectionId, revived ? "revived" : "unchanged");

            return Result.Ok(exact.Id);
        }

        var link = TelUserLink.Create(
            currentUser.TenantId,
            connectionId,
            cmd.CrmUserId,
            externalUserId,
            currentUser.Id,
            now,
            cmd.ExternalEmail,
            cmd.DefaultCallerNumber);

        db.TelUserLinks.Add(link);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Violates(ex, CrmUserIndex) || Violates(ex, ExternalUserIndex))
        {
            // The read above is the friendly answer; this is the guarantee. Two administrators
            // confirming two different pairings in the same second both pass the read.
            logger.LogWarning(
                "Lost the race to link CRM user {CrmUserId} on connection {ConnectionId}.",
                cmd.CrmUserId, connectionId);

            return Result.Fail<Guid>(TelephonyErrors.UserLinkAlreadyTaken);
        }

        logger.LogInformation(
            "Telephony link {LinkId} created for CRM user {CrmUserId} on connection {ConnectionId}.",
            link.Id, link.CrmUserId, connectionId);

        return Result.Ok(link.Id);
    }

    /// <summary>
    /// Matched by index NAME so only these two constraints are swallowed: any other violation is
    /// a bug and must keep propagating rather than being reported as "already taken".
    /// </summary>
    private static bool Violates(DbUpdateException ex, string index)
        => ex.InnerException?.Message.Contains(index, StringComparison.OrdinalIgnoreCase) == true
           || ex.Message.Contains(index, StringComparison.OrdinalIgnoreCase);
}
