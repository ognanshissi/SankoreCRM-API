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
    /// <summary>
    /// A test twin answers with its PARENT's address, modules, activation and maintenance: it is the same
    /// institution in another mode, and a parent cut off or in maintenance must not stay reachable
    /// through its twin. Only its id, name, data and test flag are its own.
    /// </summary>
    public static IQueryable<TenantResponse> SelectResponse(this IQueryable<Tenant> tenants, AdminDbContext db) =>
        tenants.Select(t => new TenantResponse(
            t.Id, t.Name, t.RootUserEmail,
            // A twin is served at its parent's address: the links its e-mails carry (KYC link,
            // ticket, back office) must open the real application. Its own column stays unusable,
            // so no lookup by address ever lands on it.
            t.ParentTenantId == null ? t.Fqdn : db.Tenants.Where(p => p.Id == t.ParentTenantId).Select(p => p.Fqdn).First(),
            t.ParentTenantId == null
                ? t.IsActive
                : t.IsActive && db.Tenants.Any(p => p.Id == t.ParentTenantId && p.IsActive),
            t.ParentTenantId == null
                ? t.IsMaintenance
                : db.Tenants.Any(p => p.Id == t.ParentTenantId && p.IsMaintenance),
            t.TrialExpiresAt, t.BlockedAt,
            t.CreatedAt, t.UpdatedAt,
            db.TenantModuleSubscriptions
                .Where(s => s.TenantId == (t.ParentTenantId ?? t.Id) && s.Status == ModuleSubscriptionStatus.Active)
                .OrderBy(s => s.ModuleCode)
                .Select(s => s.ModuleCode)
                .ToList(),
            // Deactivated once and not active again: the tenant keeps reading what it built.
            db.TenantModuleSubscriptions
                .Where(s => s.TenantId == (t.ParentTenantId ?? t.Id) && s.Status == ModuleSubscriptionStatus.Deactivated
                            && !db.TenantModuleSubscriptions.Any(a => a.TenantId == (t.ParentTenantId ?? t.Id)
                                && a.ModuleCode == s.ModuleCode && a.Status == ModuleSubscriptionStatus.Active))
                .Select(s => s.ModuleCode)
                .Distinct()
                .OrderBy(code => code)
                .ToList(),
            t.IsTest,
            t.ParentTenantId,
            db.Tenants.Where(c => c.ParentTenantId == t.Id).Select(c => (Guid?)c.Id).FirstOrDefault()));
}
