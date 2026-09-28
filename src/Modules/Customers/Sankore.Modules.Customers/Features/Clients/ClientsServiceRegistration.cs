namespace Sankore.Modules.Customers.Features.Clients;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Features.Clients.Shared;

/// <summary>
/// DI registrations specific to the <c>clients</c> zone. Called once by the module's own
/// <c>AddCustomersModule</c>, so no other zone has to know these types exist.
///
/// Handlers, validators and endpoints are NOT registered here: MediatR discovers handlers
/// by assembly scan, FluentValidation picks up the validators (including internal ones),
/// and the endpoints are mapped by <see cref="ClientsEndpoints"/>.
/// </summary>
internal static class ClientsServiceRegistration
{
    internal static IServiceCollection AddClientsServices(this IServiceCollection s)
    {
        // Scoped: both take a per-request dependency (the DbContext for the probe, the
        // Administration facade for the directory), so neither may outlive the request.
        s.AddScoped<IDuplicateProbe, DuplicateProbe>();
        s.AddScoped<IAgencyDirectory, AdministrationAgencyDirectory>();

        return s;
    }
}
