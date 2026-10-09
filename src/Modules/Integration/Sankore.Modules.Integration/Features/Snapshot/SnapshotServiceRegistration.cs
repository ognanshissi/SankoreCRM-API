namespace Sankore.Modules.Integration.Features.Snapshot;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Services of the snapshot area (INT-21). One line in <c>AddIntegrationModule</c>, the shape
/// every other area of this module uses, so several chantiers can add registrations without
/// meeting in the composition root.
/// </summary>
internal static class SnapshotServiceRegistration
{
    internal static IServiceCollection AddSnapshotServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // All scoped, like the DbContext they read and write: a projection is one unit of work per
        // customer, run from the synchronisation's own scope.
        services.AddScoped<ICbsSnapshotProjector, CbsSnapshotProjector>();
        services.AddScoped<SnapshotCodeTranslator>();
        services.AddScoped<CbsKycLevelReader>();

        return services;
    }
}
