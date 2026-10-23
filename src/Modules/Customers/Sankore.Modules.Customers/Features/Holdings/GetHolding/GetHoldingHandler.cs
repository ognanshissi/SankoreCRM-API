namespace Sankore.Modules.Customers.Features.Holdings.GetHolding;

using MediatR;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class GetHoldingHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    SubjectLabelResolver labels,
    ClientVisibility? visibility = null)
    : IRequestHandler<GetHoldingQuery, Result<HoldingDto>>
{
    public async Task<Result<HoldingDto>> Handle(GetHoldingQuery query, CancellationToken ct)
    {
        var holding = await db.ProductHoldings
            .Include(h => h.Designations)
            .FirstOrDefaultAsync(h => h.Id == query.HoldingId, ct);

        if (holding is null)
            return Result.Fail<HoldingDto>(CustomerErrors.HoldingNotFound);

        var agencyId = await (visibility?.Apply(db.Clients) ?? db.Clients)
            .Where(c => c.Id == holding.ClientId)
            .Select(c => (Guid?)c.AgencyId)
            .FirstOrDefaultAsync(ct);

        // Out of perimeter answers with the HOLDING's code, not the client's: the caller asked
        // about a holding, and a different code would say the client is missing when it is not.
        if (agencyId is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, agencyId.Value, ct))
            return Result.Fail<HoldingDto>(CustomerErrors.HoldingNotFound);

        var resolved = await labels.ResolveAsync(holding.CurrentDesignations, ct);

        return Result.Ok(HoldingMapping.ToDto(holding, resolved));
    }
}
