namespace Sankore.Modules.Integration.Features.Insurance;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Services of the insurance area. One line in <c>AddIntegrationModule</c>, the shape every other
/// area uses, so two areas written at once cannot collide in the module root.
/// </summary>
internal static class InsuranceServiceRegistration
{
    internal static IServiceCollection AddInsuranceServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped, and that matters: the probe caches one adapter lookup per connection for the
        // lifetime of a request, which is what keeps a page of fifty products from resolving the
        // same adapter fifty times. A singleton would carry a verdict across requests, and the
        // capability depends on the connection's own settings, which an administrator can edit.
        services.AddScoped<InsurerPricingProbe>();

        // Reaches M12 through IAdministrationModule — a PublicApi contract, never its assembly.
        services.AddScoped<LinkedCreditProductCheck>();

        // Scoped for the same reason as the pricing probe, one level up: it caches one catalogue
        // lookup per product code for the lifetime of a request, and a singleton would carry a
        // verdict across requests while an administrator retires the entry behind it.
        services.AddScoped<CrmProductCatalogue>();

        return services;
    }
}
