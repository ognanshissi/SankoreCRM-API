namespace Sankore.Modules.Customers.Features.Timeline.Shared;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Agency-perimeter check for the reads of this zone that hang off a single client.
///
/// A client outside the caller's perimeter and a client that does not exist must be
/// INDISTINGUISHABLE: both return <c>false</c> here and the handler answers
/// <c>CLIENT_NOT_FOUND</c> (404), never 403 — a 403 would confirm that the record exists,
/// which is itself a disclosure.
///
/// The <c>AgencyAuthorizationBehavior</c> cannot do this: the agency is not a field of the
/// query, it has to be read from the client row first.
/// </summary>
internal static class TimelineClientScope
{
    internal static async Task<bool> CanReadClientAsync(
        CustomersDbContext db,
        IAgencyScopeProvider agencyScope,
        ICurrentUser currentUser,
        Guid clientId,
        CancellationToken ct)
    {
        // Ambient tenant filter applies: this path is always an authenticated HTTP read.
        var agencyId = await db.Clients
            .Where(c => c.Id == clientId)
            .Select(c => (Guid?)c.AgencyId)
            .FirstOrDefaultAsync(ct);

        if (agencyId is null) return false;

        return await agencyScope.CanAccessAgencyAsync(
            currentUser.TenantId, currentUser.Id, agencyId.Value, ct);
    }
}
