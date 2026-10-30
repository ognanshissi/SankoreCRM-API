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
    DateTime UpdatedAt,
    /// <summary>
    /// Codes of the paid modules in force (<c>TenantModules</c>). Sankore.Api reads it into
    /// <c>TenantInfo.ActiveModules</c> by name, so the name is part of the contract.
    /// </summary>
    IReadOnlyList<string> ActiveModules,
    /// <summary>
    /// Modules the tenant deactivated and has not switched on again: their screens stay open for
    /// CONSULTATION (product owner, 2026-10-23). Read into <c>TenantInfo.ReadOnlyModules</c> by name.
    /// </summary>
    IReadOnlyList<string> ReadOnlyModules,
    /// <summary>
    /// A recette or demo institution (data-purge lot 4). Read into <c>TenantInfo.IsTest</c> by name:
    /// the API checks it again before wiping anything.
    /// </summary>
    bool IsTest,
    /// <summary>
    /// Sandbox lot 2. On a test twin, the live tenant it belongs to; read into
    /// <c>TenantInfo.ParentTenantId</c>. Null on every other tenant.
    /// </summary>
    Guid? ParentTenantId = null,
    /// <summary>On a live tenant, its test twin once created; read into <c>TenantInfo.TestTenantId</c>.</summary>
    Guid? TestTenantId = null);

internal sealed class GetTenantHandler(AdminDbContext db) : IRequestHandler<GetTenantQuery, TenantResponse?>
{
    public async Task<TenantResponse?> Handle(GetTenantQuery request, CancellationToken ct)
    {
        return await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == request.Id)
            .SelectResponse(db)
            .FirstOrDefaultAsync(ct);
    }
}
