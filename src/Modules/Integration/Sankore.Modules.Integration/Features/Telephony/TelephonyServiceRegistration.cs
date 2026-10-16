namespace Sankore.Modules.Integration.Features.Telephony;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What the telephony area needs in the container. One call, wired from
/// <c>IntegrationModule.AddIntegrationModule</c>.
///
/// <para>
/// The two jobs are <c>AddTransient</c> and not scoped, which is what Hangfire's activator
/// expects: it resolves the job type per execution from the root provider, and a scoped
/// registration there resolves from the root scope and lives for the life of the process.
/// </para>
/// </summary>
internal static class TelephonyServiceRegistration
{
    internal static IServiceCollection AddTelephonyServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped, both of them: they take IntegrationDbContext and the keyed encryptors, and a
        // singleton holding a DbContext is the one lifetime mistake this repo cannot survive.
        services.AddScoped<TelephonyAccess>();
        services.AddScoped<TelephonyNumberProtector>();
        services.AddScoped<TelephonyPayloadProtector>();
        services.AddScoped<Journal.TelephonyCallMatcher>();
        services.AddScoped<Journal.TelCallJournalWriter>();

        services.AddTransient<UserLinks.CheckUserLinks.CheckTelephonyUserLinksOrchestrator>();
        services.AddTransient<UserLinks.CheckUserLinks.CheckTelephonyUserLinksJob>();
        services.AddTransient<Webhook.ProcessTelephonyWebhookJob>();

        return services;
    }
}
