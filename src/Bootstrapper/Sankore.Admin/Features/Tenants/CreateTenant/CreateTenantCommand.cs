using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants.CreateTenant;

public record CreateTenantCommand(
    string Name,
    string RootUserEmail,
    string Fqdn,
    DateTimeOffset? TrialExpiresAt) : IRequest<CreateTenantResponse>;


public record CreateTenantResponse(Guid Id);

internal sealed class CreateTenantHandler(AdminDbContext db) : IRequestHandler<CreateTenantCommand, CreateTenantResponse>
{
    public async Task<CreateTenantResponse> Handle(CreateTenantCommand request, CancellationToken ct)
    {
        var existing = await db.Tenants
            .AnyAsync(t => t.Fqdn == request.Fqdn, ct);

        if (existing)
            throw new InvalidOperationException($"A tenant with FQDN '{request.Fqdn}' already exists.");

        var tenant = Tenant.Create(request.Name, request.RootUserEmail, request.Fqdn, request.TrialExpiresAt);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        return new CreateTenantResponse(tenant.Id);
    }
}
