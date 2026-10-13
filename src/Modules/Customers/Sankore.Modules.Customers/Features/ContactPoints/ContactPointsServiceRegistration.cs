using Microsoft.Extensions.DependencyInjection;

namespace Sankore.Modules.Customers.Features.ContactPoints;

/// <summary>
/// DI hook for the contact-point zone. Handlers and validators are discovered by assembly
/// scanning; what is registered here is the one service the three write slices share.
/// </summary>
internal static class ContactPointsServiceRegistration
{
    internal static IServiceCollection AddContactPointsServices(this IServiceCollection s)
    {
        // Announces a change of reachable contact details through the outbox (KRM-B-04). Shared by
        // add, close and promote so the three agree on the field name a consumer matches against.
        s.AddScoped<Shared.ContactPointChangeAnnouncer>();
        return s;
    }
}
