using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants.CreateTenant;

public record CreateTenantCommand(
    string Name,
    string RootUserEmail,
    string Fqdn,
    DateTimeOffset? TrialExpiresAt) : IRequest<Guid>;

internal sealed class CreateTenantHandler(AdminDbContext db) : IRequestHandler<CreateTenantCommand, Guid>
{
    public async Task<Guid> Handle(CreateTenantCommand request, CancellationToken ct)
    {
        var existing = await db.Tenants
            .AnyAsync(t => t.Fqdn == request.Fqdn, ct);

        if (existing)
            throw new InvalidOperationException($"A tenant with FQDN '{request.Fqdn}' already exists.");

        var tenant = Tenant.Create(request.Name, request.RootUserEmail, request.Fqdn, request.TrialExpiresAt);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        return tenant.Id;
    }
}
