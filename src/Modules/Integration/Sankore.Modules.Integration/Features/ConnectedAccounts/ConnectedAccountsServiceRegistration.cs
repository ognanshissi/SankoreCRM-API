namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sankore.Modules.Integration.Domain;

internal static class ConnectedAccountsServiceRegistration
{
    internal static IServiceCollection AddConnectedAccountsServices(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ConnectedAccountOptions>(config.GetSection(ConnectedAccountOptions.Section));

        services.AddHttpClient<MicrosoftConnectedAccountProvider>(c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddHttpClient<GoogleConnectedAccountProvider>(c => c.Timeout = TimeSpan.FromSeconds(20));

        // The real providers FIRST: ConnectedAccountProviders takes the first AVAILABLE one, so a
        // configured client id always wins over the simulation.
        services.AddScoped<IConnectedAccountProvider>(sp => sp.GetRequiredService<MicrosoftConnectedAccountProvider>());
        services.AddScoped<IConnectedAccountProvider>(sp => sp.GetRequiredService<GoogleConnectedAccountProvider>());

        if (config.GetValue<bool>($"{ConnectedAccountOptions.Section}:UseFakeProviders"))
        {
            services.AddScoped<IConnectedAccountProvider>(sp =>
                new FakeConnectedAccountProvider(ConnectedAccountProvider.Microsoft, sp.GetRequiredService<IHostEnvironment>()));
            services.AddScoped<IConnectedAccountProvider>(sp =>
                new FakeConnectedAccountProvider(ConnectedAccountProvider.Google, sp.GetRequiredService<IHostEnvironment>()));
        }

        services.AddScoped<ConnectedAccountProviders>();
        services.AddScoped<ConnectedAccountOAuth>();
        services.AddScoped<ConnectedAccountReturnOrigins>();
        services.AddScoped<ConnectedAccountTokens>();

        // Agendas (lot 2).
        services.AddScoped<CalendarEventProtector>();
        services.AddScoped<ConnectedCalendarSync>();
        services.AddScoped<SyncConnectedCalendarsJob>();
        services.AddScoped<SyncConnectedAccountJob>();
        services.AddScoped<SyncConnectedCalendarsOrchestrator>();

        // Copies of SANKORÉ rendez-vous (lot 4).
        services.AddScoped<AppointmentCalendarCopies>();
        services.AddScoped<ReconcileUserAppointmentCopiesJob>();

        return services;
    }
}
