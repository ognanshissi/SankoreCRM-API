namespace Sankore.Modules.Integration.Features.Mappings;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Modules.Integration.Features.Mappings.ListUnmappedCodes;

/// <summary>
/// Services of the mappings area. Called from <c>AddIntegrationModule</c>, one line, so this
/// area's registrations are not scattered through the module root.
/// </summary>
internal static class MappingsServiceRegistration
{
    internal static IServiceCollection AddMappingsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped, like the DbContext it holds: this is what every adapter and the dispatcher call
        // to translate a code, in both directions (INT-04 criterion 4, INT-21).
        services.AddScoped<MappingResolver>();

        // Behind an interface so the unmapped-codes screen can be served from a real contract one
        // domain at a time — see ContractCrmCodeCatalog for what is reachable today.
        services.AddScoped<ICrmCodeCatalog, ContractCrmCodeCatalog>();

        services.AddTransient<MappingImportReader>();

        // Stateless and pure, hence a singleton; it is the same validator for the import and for
        // the dry run, which is what makes the two reports comparable.
        services.AddSingleton<MappingImportValidator>();

        return services;
    }
}
