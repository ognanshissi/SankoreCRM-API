namespace Sankore.Modules.Integration.Features.Connections;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What the connections area needs in the container. One call, wired from
/// <c>IntegrationModule.AddIntegrationModule</c>.
///
/// <para>
/// It registers nothing today, and that is a statement rather than an oversight. Every slice of
/// the area runs on services the module already provides — <c>IntegrationDbContext</c>,
/// <c>IntegrationAdapterResolver</c>, <c>ISecretsModule</c>, <c>TimeProvider</c> — and its
/// handlers and validators are found by the assembly scans in <c>AddIntegrationModule</c>.
/// </para>
///
/// <para>
/// The one thing the area needs that is NOT a module service is the HTTP side of
/// <c>ConnectionSettings</c>' polymorphism, and the host owns it: <c>Program.cs</c> adds
/// <c>Sankore.Api.Infrastructure.ConnectionSettingsJsonConverter</c> to
/// <c>ConfigureHttpJsonOptions</c>, where the serializer is configured once. A module-side twin
/// would be a second converter for the same type, and which of the two won would depend on the
/// order the two registrations happened to run in.
/// </para>
///
/// <para>
/// The seam is kept all the same: it is where a service of this area is registered the day one
/// exists, so adding one never means editing <c>IntegrationModule</c> again.
/// </para>
/// </summary>
internal static class ConnectionsServiceRegistration
{
    internal static IServiceCollection AddConnectionsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services;
    }
}
