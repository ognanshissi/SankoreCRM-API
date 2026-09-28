namespace Sankore.Modules.Customers.Features.Clients.GetClient;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// US-M01-BE-10.
///
/// Two invariants carry this handler:
/// <list type="bullet">
/// <item>Out of perimeter answers exactly like non-existent — <c>CLIENT_NOT_FOUND</c>,
/// which the endpoint turns into a 404. A 403 would tell the caller "this client exists,
/// just not in your branch", and in a network of agencies that alone is information
/// (a competitor's client list is worth knowing about).</item>
/// <item>Not one protected value leaves in clear text. <see cref="ClientDtoMapper"/> is the
/// only decryption site, and it masks in the same expression.</item>
/// </list>
/// </summary>
internal sealed class GetClientHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IFieldEncryptor encryptor
) : IRequestHandler<GetClientQuery, Result<ClientDetailDto>>
{
    public async Task<Result<ClientDetailDto>> Handle(GetClientQuery query, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;

        // NoTracking by default (DbContext-wide): this is a pure read.
        var client = await db.Clients
            .Include(c => c.ContactPoints)
            .Include(c => c.StatusHistory)
            .FirstOrDefaultAsync(c => c.Id == query.ClientId, ct);

        if (client is null)
            return Result.Fail<ClientDetailDto>(CustomerErrors.ClientNotFound);

        if (!await agencyScope.CanAccessAgencyAsync(tenantId, actor, client.AgencyId, ct))
            return Result.Fail<ClientDetailDto>(CustomerErrors.ClientNotFound);

        // A merged record still answers, but it points at its survivor: every caller
        // holding the absorbed id must be able to follow the link in one round-trip
        // instead of guessing where the data went.
        MergedIntoDto? mergedInto = null;
        if (client.MergedIntoId.HasValue)
        {
            mergedInto = await db.Clients
                .Where(c => c.Id == client.MergedIntoId.Value)
                .Select(c => new MergedIntoDto(c.Id, c.ClientNumber))
                .FirstOrDefaultAsync(ct);
        }

        return Result.Ok(ClientDtoMapper.ToDetail(client, encryptor, mergedInto));
    }
}
