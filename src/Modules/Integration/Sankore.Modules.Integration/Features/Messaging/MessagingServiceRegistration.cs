namespace Sankore.Modules.Integration.Features.Messaging;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Features.Messaging.Webhook;
using Sankore.Modules.Integration.Infrastructure;

/// <summary>The messaging family's services (WA lot 1).</summary>
internal static class MessagingServiceRegistration
{
    internal static IServiceCollection AddMessagingServices(this IServiceCollection services)
    {
        // A factory, for the cache alone: IDistributedCache is Redis in a deployment and absent in
        // the module's tests, and the directory works without it — it simply asks the database.
        services.AddScoped(sp => new MessagingChannels(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<IntegrationAdapterResolver>(),
            sp.GetService<IDistributedCache>(),
            sp.GetRequiredService<ILogger<MessagingChannels>>()));

        services.AddScoped<ProcessMessagingWebhookJob>();

        return services;
    }
}
