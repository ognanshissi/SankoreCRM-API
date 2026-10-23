using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Admin.Features.Tenants.CreateTenant;

/// <param name="ActiveModules">
/// Paid modules switched on at creation, as <see cref="TenantModules"/> codes — the creation
/// profile (« Caisse avec assurance » = both). Null or empty creates a tenant on the socle alone.
/// </param>
/// <param name="AdminId">The platform administrator creating it; null when the service key does.</param>
public record CreateTenantCommand(
    string Name,
    string RootUserEmail,
    string Fqdn,
    DateTimeOffset? TrialExpiresAt,
    IReadOnlyList<string>? ActiveModules = null,
    Guid? AdminId = null) : IRequest<CreateTenantResponse>;


public record CreateTenantResponse(Guid Id);

internal sealed class CreateTenantHandler(AdminDbContext db) : IRequestHandler<CreateTenantCommand, CreateTenantResponse>
{
    public async Task<CreateTenantResponse> Handle(CreateTenantCommand request, CancellationToken ct)
    {
        var existing = await db.Tenants
            .AnyAsync(t => t.Fqdn == request.Fqdn, ct);

        if (existing)
            throw new InvalidOperationException($"A tenant with FQDN '{request.Fqdn}' already exists.");

        var modules = (request.ActiveModules ?? []).Distinct(StringComparer.Ordinal).ToList();
        var unknown = modules.Where(m => !TenantModules.IsKnown(m)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"Unknown module(s): {string.Join(", ", unknown)}.");

        var tenant = Tenant.Create(request.Name, request.RootUserEmail, request.Fqdn, request.TrialExpiresAt);
        db.Tenants.Add(tenant);

        var now = DateTimeOffset.UtcNow;
        foreach (var module in modules)
            db.TenantModuleSubscriptions.Add(TenantModuleSubscription.ActivateDirect(
                tenant.Id, module, request.AdminId, ModuleSubscriptionSource.Direct, now));

        await db.SaveChangesAsync(ct);
        return new CreateTenantResponse(tenant.Id);
    }
}
