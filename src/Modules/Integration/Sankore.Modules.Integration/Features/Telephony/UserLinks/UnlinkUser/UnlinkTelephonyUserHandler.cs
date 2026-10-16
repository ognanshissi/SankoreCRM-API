namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.UnlinkUser;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UnlinkTelephonyUserHandler(
    IntegrationDbContext db,
    ILogger<UnlinkTelephonyUserHandler> logger)
    : IRequestHandler<UnlinkTelephonyUserCommand, Result>
{
    public async Task<Result> Handle(UnlinkTelephonyUserCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var link = await db.TelUserLinks
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LinkId, ct);

        if (link is null)
            return Result.Fail(TelephonyErrors.UserLinkNotFound);

        // Already disabled: the caller's intent is satisfied. Refusing would make a double-click
        // read as an error, and re-writing the reason would overwrite the first explanation with
        // the second — which is the one a later reader does not need.
        if (link.Status == TelUserLinkStatus.Disabled)
            return Result.Ok();

        var disabled = link.Disable(cmd.Reason);

        if (disabled.IsFailure)
            return disabled;

        await db.SaveChangesAsync(ct);

        // The reason is NOT logged. It is free text an administrator typed about a named person,
        // and the audit row already carries it under the permission that gates reading it.
        logger.LogInformation(
            "Telephony link {LinkId} for CRM user {CrmUserId} unlinked.", link.Id, link.CrmUserId);

        return Result.Ok();
    }
}
