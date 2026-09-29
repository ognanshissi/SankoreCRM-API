namespace Sankore.Modules.Customers.Features.Import;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Features.Import.Readers;
using Sankore.Modules.Customers.Features.Import.ValidateImport;

internal static class ImportServiceRegistration
{
    internal static IServiceCollection AddImportServices(this IServiceCollection services)
    {
        // Readers are resolved by source type inside the job, so they are registered as
        // themselves rather than behind the interface.
        services.AddTransient<ClientFileImportReader>();
        services.AddTransient<ClientGoogleSheetsImportReader>();
        services.AddScoped<ClientImportValidator>();
        services.AddTransient<ProcessClientImportJob>();

        return services;
    }
}
