using Microsoft.Extensions.DependencyInjection;

namespace Sankore.Modules.Customers.Features.ContactPoints;

/// <summary>
/// DI hook for the contact-point zone. The slices only depend on services already
/// registered by the module (DbContext, ICurrentUser, IAgencyScopeProvider,
/// IFieldEncryptor, IBlindIndexer) and their handlers/validators are discovered by
/// assembly scanning, so there is nothing zone-specific to register yet. The method
/// exists so the module never has to be edited when that changes.
/// </summary>
internal static class ContactPointsServiceRegistration
{
    internal static IServiceCollection AddContactPointsServices(this IServiceCollection s) => s;
}
