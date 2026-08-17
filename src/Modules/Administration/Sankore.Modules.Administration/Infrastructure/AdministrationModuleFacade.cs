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

    public async Task<AgentSummary?> GetAgentAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await db.Users.SingleOrDefaultAsync(u => u.Id == agentId, ct);
        return agent is null ? null : ToSummary(agent);
    }

    private static AgentSummary ToSummary(AppUser u) => new(
        Id: u.Id,
        FullName: u.FullName,
        AgencyId: u.AgencyId,
        SpokenLanguages: u.SpokenLanguages,
        Specialties: u.Specialties,
        CurrentLocation: u.LastKnownLocation,
        ActiveLeadsCount: u.ActiveLeadsCount,
        HotLeadsCount: u.HotLeadsCount,
        ConversionRate30d: u.ConversionRate30D,
        IsAvailable: u.IsAvailable);
}
