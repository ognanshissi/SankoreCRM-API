namespace Sankore.Modules.Integration.Features.Onboarding;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Everything INT-14's chain puts in the container, in the shape every other area of this module
/// uses: one call from <c>AddIntegrationModule</c>, so two areas written at once cannot collide.
///
/// <para>
/// The two consumers are NOT registered here. A MassTransit consumer is registered on the bus by
/// the host (<c>AddConsumer&lt;T&gt;</c>), which is also what binds its receive endpoint — a
/// module that registered them in the container would get a resolvable type bound to no queue,
/// and nothing in the logs to say so.
/// </para>
/// </summary>
internal static class OnboardingServiceRegistration
{
    internal static IServiceCollection AddOnboardingServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // TryAdd so a module that later owns the product chosen at enrolment can register its own
        // selector before this call and win, exactly as the batch socle wins over
        // UnavailableBatchFileEnlister in the commands area.
        services.TryAddScoped<IOnboardingProductSelector, NoOnboardingProductSelector>();

        return services;
    }
}
