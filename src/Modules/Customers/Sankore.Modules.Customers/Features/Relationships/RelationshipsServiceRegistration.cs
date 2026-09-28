using Microsoft.Extensions.DependencyInjection;

namespace Sankore.Modules.Customers.Features.Relationships;

/// <summary>
/// DI hook for the relationship zone. Its slices only use services the module
/// already registers (DbContext, ICurrentUser, IAgencyScopeProvider, IFieldEncryptor,
/// IBlindIndexer and the keyed IEventPublisher), and handlers/validators are found by
/// assembly scanning — so there is nothing zone-specific to register yet. The method
/// exists so the module never has to be edited when that changes.
/// </summary>
internal static class RelationshipsServiceRegistration
{
    internal static IServiceCollection AddRelationshipsServices(this IServiceCollection s) => s;
}
