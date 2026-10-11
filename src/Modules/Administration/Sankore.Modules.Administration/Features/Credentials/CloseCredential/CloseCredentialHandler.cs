namespace Sankore.Modules.Administration.Features.Credentials.CloseCredential;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Features.Credentials;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class CloseCredentialHandler(
    AdministrationDbContext db, ICurrentUser currentUser, TimeProvider clock)
    : IRequestHandler<CloseCredentialCommand, Result>
{
    public async Task<Result> Handle(CloseCredentialCommand command, CancellationToken ct)
    {
        var credential = await db.DistributionCredentials
            .AsTracking()
            .FirstOrDefaultAsync(c => c.UserId == command.UserId && c.ValidTo == null, ct);

        if (credential is null)
            return Result.Fail(CredentialErrors.NotFound);

        var close = credential.Close(command.Reason, currentUser.Id, clock.GetUtcNow());
        if (close.IsFailure)
            return close;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
