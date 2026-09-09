using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants.GetTenant;

public record GetTenantQuery(Guid Id) : IRequest<TenantResponse?>;

public record TenantResponse(
    Guid Id,
    string Name,
    string RootUserEmail,
    string Fqdn,
    bool IsActive,
    bool IsMaintenance,
    DateTimeOffset? TrialExpiresAt,
    DateTimeOffset? BlockedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal sealed class GetTenantHandler(AdminDbContext db) : IRequestHandler<GetTenantQuery, TenantResponse?>
{
    public async Task<TenantResponse?> Handle(GetTenantQuery request, CancellationToken ct)
    {
        return await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == request.Id)
            .Select(t => new TenantResponse(
                t.Id, t.Name, t.RootUserEmail, t.Fqdn,
                t.IsActive, t.IsMaintenance, t.TrialExpiresAt, t.BlockedAt,
                t.CreatedAt, t.UpdatedAt))
            .FirstOrDefaultAsync(ct);
    }
}
