using Microsoft.AspNetCore.Identity;

namespace Sankore.Modules.Administration.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.PublicApi;

/// <summary>
/// The single door into the Users module for the rest of the system.
/// Internal on purpose: consumers depend on IAdministrationModule (PublicApi),
/// never on this class or on AdministrationDbContext directly.
/// </summary>
internal sealed class AdministrationModuleFacade(AdministrationDbContext db, UserManager<AppUser> userManager) : IAdministrationModule
{
    // public async Task<IReadOnlyList<AgentSummary>> GetAvailableAgentsAsync(
    //     Guid tenantId, Guid? agencyId, CancellationToken ct)
    // {
    //     var query = db.Users
    //         .Where(u => u.TenantId == tenantId
    //                  && u.Role == UserRole.CommercialAgent
    //                  && u.IsAvailable);
    //
    //     if (agencyId.HasValue)
    //         query = query.Where(u => u.AgencyId == agencyId.Value);
    //
    //     var agents = await query.ToListAsync(ct);
    //
    //     return agents.Select(ToSummary).ToList();
    // }

    public Task<IReadOnlyList<AgentSummary>> GetAvailableAgentsAsync(Guid tenantId, Guid? agencyId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public async Task<TenantNotificationConfigDto?> GetNotificationConfigAsync(
        Guid tenantId, CancellationToken ct)
    {
        var s = await db.TenantNotificationSettings
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);

        return s is null ? null : new TenantNotificationConfigDto(
            s.ProviderType,
            s.UseDefaultPlatformProvider,
            s.FromEmail,
            s.FromName,
            s.ReplyToEmail,
            s.SendingDomain,
            s.CredentialVaultPath,
            s.MonthlyQuotaLimit);
    }

    public async Task<string?> GetProductCategoryAsync(
        Guid tenantId, string productCode, CancellationToken ct)
    {
        var product = await db.ProductSpecialities
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId
                                   && p.Code == productCode.ToUpperInvariant(), ct);

        return product?.Category.ToString();
    }

    public async Task<IReadOnlyList<Guid>> GetTeamAgentIdsAsync(
        Guid tenantId, Guid supervisorId, CancellationToken ct)
    {
        var supervisor = await db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == supervisorId && u.TenantId == tenantId, ct);

        if (supervisor is null)
            return Array.Empty<Guid>();

        // Super-users see all agents for the tenant.
        if (supervisor.IsSuperUser || supervisor.AgencyId is null)
        {
            return await db.Users
                .IgnoreQueryFilters()
                .Where(u => u.TenantId == tenantId && u.Id != supervisorId)
                .Select(u => u.Id)
                .ToListAsync(ct);
        }

        // Collect the supervisor's agency and all descendant agencies.
        var teamAgencyIds = new HashSet<Guid> { supervisor.AgencyId.Value };
        var frontier = new Queue<Guid>();
        frontier.Enqueue(supervisor.AgencyId.Value);

        while (frontier.Count > 0)
        {
            var parentId = frontier.Dequeue();
            var children = await db.Agencies
                .IgnoreQueryFilters()
                .Where(a => a.TenantId == tenantId && a.ParentAgencyId == parentId && a.IsActive)
                .Select(a => a.Id)
                .ToListAsync(ct);

            foreach (var childId in children)
            {
                if (teamAgencyIds.Add(childId))
                    frontier.Enqueue(childId);
            }
        }

        return await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId
                     && u.AgencyId.HasValue
                     && teamAgencyIds.Contains(u.AgencyId.Value)
                     && u.Id != supervisorId)
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    public async Task<AgentSummary?> GetAgentAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await db.Users.SingleOrDefaultAsync(u => u.Id == agentId, ct);
        return agent is null ? null : ToSummary(agent);
    }

    private static AgentSummary ToSummary(AppUser u) => new(
        Id: u.Id,
        FullName: u.FullName,
        AgencyId: u.AgencyId ?? Guid.Empty,
        SpokenLanguages: u.SpokenLanguages,
        Specialties: u.Specialties,
        CurrentLocation: u.LastKnownLocation,
        ActiveLeadsCount: u.ActiveLeadsCount,
        HotLeadsCount: u.HotLeadsCount,
        ConversionRate30d: u.ConversionRate30D,
        IsAvailable: u.IsAvailable);
}
