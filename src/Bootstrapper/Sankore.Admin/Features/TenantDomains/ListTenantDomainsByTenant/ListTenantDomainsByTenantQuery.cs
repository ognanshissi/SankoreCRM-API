using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.TenantDomains.ListTenantDomainsByTenant;

using Sankore.Admin.Features.TenantDomains;

public record ListTenantDomainsByTenantQuery(Guid TenantId) : IRequest<List<TenantDomainResponse>>;

internal sealed class ListTenantDomainsByTenantHandler(AdminDbContext db)
    : IRequestHandler<ListTenantDomainsByTenantQuery, List<TenantDomainResponse>>
{
    public async Task<List<TenantDomainResponse>> Handle(
        ListTenantDomainsByTenantQuery request, CancellationToken ct)
    {
        return await db.TenantDomains
            .AsNoTracking()
            .Where(d => d.TenantId == request.TenantId)
            .OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.Fqdn)
            .Select(d => new TenantDomainResponse(
                d.Id, d.TenantId, d.Fqdn, d.IsPrimary, d.IsActive,
                d.ValidFrom, d.ValidTo, d.CreatedAt, d.UpdatedAt))
            .ToListAsync(ct);
    }
}