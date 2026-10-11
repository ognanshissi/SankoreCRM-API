namespace Sankore.Modules.Administration.Features.Credentials.ListCredentials;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListCredentialsHandler(AdministrationDbContext db, TimeProvider clock)
    : IRequestHandler<ListCredentialsQuery, Result<IReadOnlyList<CredentialDto>>>
{
    public async Task<Result<IReadOnlyList<CredentialDto>>> Handle(
        ListCredentialsQuery query, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // The tenant query filter does the isolation.
        var rows = await db.DistributionCredentials
            .Where(c => c.UserId == query.UserId)
            // Current first, then most recently superseded: the row an operator acts on is the
            // current one, and the history below it reads backwards in time the way a register
            // does.
            .OrderBy(c => c.ValidTo == null ? 0 : 1)
            .ThenByDescending(c => c.ValidFrom)
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<CredentialDto>>(
            [.. rows.Select(c => CredentialDto.From(c, today))]);
    }
}
