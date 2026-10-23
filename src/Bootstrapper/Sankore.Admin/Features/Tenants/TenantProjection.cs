using Sankore.Admin.Domain;
using Sankore.Admin.Features.Tenants.GetTenant;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants;

/// <summary>
/// The one Tenant → <see cref="TenantResponse"/> projection. It used to be written out in each of
/// the three read handlers; a field added to one and not the others would have reached Sankore.Api
/// through one lookup only — and Sankore.Api deserialises <c>TenantInfo</c> straight from this
/// shape, so the same tenant would have looked different depending on how it was resolved.
/// </summary>
internal static class TenantProjection
{
    public static IQueryable<TenantResponse> SelectResponse(this IQueryable<Tenant> tenants, AdminDbContext db) =>
        tenants.Select(t => new TenantResponse(
            t.Id, t.Name, t.RootUserEmail, t.Fqdn,
            t.IsActive, t.IsMaintenance, t.TrialExpiresAt, t.BlockedAt,
            t.CreatedAt, t.UpdatedAt,
            db.TenantModuleSubscriptions
                .Where(s => s.TenantId == t.Id && s.Status == ModuleSubscriptionStatus.Active)
                .OrderBy(s => s.ModuleCode)
                .Select(s => s.ModuleCode)
                .ToList(),
            // Deactivated once and not active again: the tenant keeps reading what it built.
            db.TenantModuleSubscriptions
                .Where(s => s.TenantId == t.Id && s.Status == ModuleSubscriptionStatus.Deactivated
                            && !db.TenantModuleSubscriptions.Any(a => a.TenantId == t.Id
                                && a.ModuleCode == s.ModuleCode && a.Status == ModuleSubscriptionStatus.Active))
                .Select(s => s.ModuleCode)
                .Distinct()
                .OrderBy(code => code)
                .ToList()));
}
