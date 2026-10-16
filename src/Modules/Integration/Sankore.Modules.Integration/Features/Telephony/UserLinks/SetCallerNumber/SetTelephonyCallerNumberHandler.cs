namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.SetCallerNumber;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class SetTelephonyCallerNumberHandler(IntegrationDbContext db)
    : IRequestHandler<SetTelephonyCallerNumberCommand, Result>
{
    public async Task<Result> Handle(SetTelephonyCallerNumberCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // No tenant predicate and no 403: the global query filter scopes it, so another tenant's
        // link answers "not found" and the existence of the row never leaks.
        var link = await db.TelUserLinks
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LinkId, ct);

        if (link is null)
            return Result.Fail(TelephonyErrors.UserLinkNotFound);

        // Allowed on a Broken link on purpose: an administrator repairing one often corrects the
        // number at the same time, and refusing here would make the repair a two-step dance.
        link.SetDefaultCallerNumber(cmd.DefaultCallerNumber);

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
