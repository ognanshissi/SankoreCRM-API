namespace Sankore.Modules.Customers.Features.Duplicates.Merge.GetClientMerge;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class GetClientMergeHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope)
    : IRequestHandler<GetClientMergeQuery, Result<ClientMergeRequestDto>>
{
    public async Task<Result<ClientMergeRequestDto>> Handle(GetClientMergeQuery query, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        var request = await db.ClientMergeRequests
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == query.MergeRequestId, ct);

        if (request is null)
            return Result.Fail<ClientMergeRequestDto>(CustomerErrors.MergeRequestNotFound);

        var clients = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && (c.Id == request.SurvivorClientId || c.Id == request.AbsorbedClientId))
            .ToListAsync(ct);

        // Out of perimeter reads as "not found", never 403: the caller must not learn that a merge
        // request exists between two clients of a branch they cannot see.
        foreach (var client in clients)
        {
            if (!await agencyScope.CanAccessAgencyAsync(tenantId, currentUser.Id, client.AgencyId, ct))
                return Result.Fail<ClientMergeRequestDto>(CustomerErrors.MergeRequestNotFound);
        }

        var survivor = clients.FirstOrDefault(c => c.Id == request.SurvivorClientId);
        var absorbed = clients.FirstOrDefault(c => c.Id == request.AbsorbedClientId);

        return Result.Ok(ClientMergeRequestMapper.ToDto(request, survivor, absorbed));
    }
}
