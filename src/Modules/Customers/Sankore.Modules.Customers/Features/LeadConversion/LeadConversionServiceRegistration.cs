namespace Sankore.Modules.Customers.Features.LeadConversion;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Infrastructure;

/// <summary>
/// DI registrations owned by the LeadConversion zone. Called from
/// <c>CustomersModule.AddCustomersModule(...)</c> alongside the other zones'
/// <c>Add&lt;Area&gt;Services</c> methods.
///
/// <para>
/// Scoped, like every other per-request service of the module: the handler it forwards to
/// resolves <c>CustomersDbContext</c>, whose lifetime is the request / job scope.
/// </para>
///
/// <para>
/// The command handler and its validator need no entry here — MediatR discovers handlers with
/// <c>RegisterServicesFromAssembly</c> and FluentValidation picks the validator up through
/// <c>AddValidatorsFromAssembly(..., includeInternalTypes: true)</c>.
/// </para>
/// </summary>
internal static class LeadConversionServiceRegistration
{
    internal static IServiceCollection AddLeadConversionServices(this IServiceCollection services)
    {
        services.AddScoped<ILeadConversionService, LeadConversionService>();

        return services;
    }
}
