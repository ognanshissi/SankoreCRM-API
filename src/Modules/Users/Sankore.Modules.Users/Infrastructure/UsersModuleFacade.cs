namespace Sankore.Modules.Users.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Users.Domain;
using Sankore.Modules.Users.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The single door into the Users module for the rest of the system.
/// Internal on purpose: consumers depend on IUsersModule (PublicApi),
/// never on this class or on UsersDbContext directly.
/// </summary>
internal sealed class UsersModuleFacade(UsersDbContext db) : IUsersModule
{
    public async Task<IReadOnlyList<AgentSummary>> GetAvailableAgentsAsync(
        Guid tenantId, Guid? agencyId, CancellationToken ct)
    {
        var query = db.Users
            .Where(u => u.TenantId == tenantId
                     && u.Role == UserRole.CommercialAgent
                     && u.IsAvailable);

        if (agencyId.HasValue)
            query = query.Where(u => u.AgencyId == agencyId.Value);

        var agents = await query.ToListAsync(ct);

        return agents.Select(ToSummary).ToList();
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
        ConversionRate30d: u.ConversionRate30d,
        IsAvailable: u.IsAvailable);
}
