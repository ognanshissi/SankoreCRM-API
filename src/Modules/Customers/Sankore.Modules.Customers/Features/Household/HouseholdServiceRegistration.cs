namespace Sankore.Modules.Customers.Features.Household;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Features.Household.Shared;

/// <summary>
/// DI for the household zone (BNF-B-13). <see cref="HouseholdWriteContext"/> is the only
/// zone-specific service: it sequences load → perimeter → PEP window → commit-with-recalculation,
/// which every write of the zone runs and none of them may skip.
/// </summary>
internal static class HouseholdServiceRegistration
{
    internal static IServiceCollection AddHouseholdServices(this IServiceCollection services)
        => services.AddScoped<HouseholdWriteContext>();
}
