namespace Sankore.Modules.Customers.Features.LegalEntities;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI of the LegalEntities area. Nothing to register today: every collaborator the four
/// handlers need (<c>IDuplicateProbe</c>, <c>IAgencyDirectory</c>,
/// <c>IClientNumberGenerator</c>, <c>ICustomerSettings</c>, crypto, phonetic keys) is
/// owned by the Clients area or by the module foundation, and the handlers and validators
/// are discovered by assembly scanning (MediatR + FluentValidation with
/// <c>includeInternalTypes: true</c>).
/// </summary>
internal static class LegalEntitiesServiceRegistration
{
    internal static IServiceCollection AddLegalEntitiesServices(this IServiceCollection s) => s;
}
