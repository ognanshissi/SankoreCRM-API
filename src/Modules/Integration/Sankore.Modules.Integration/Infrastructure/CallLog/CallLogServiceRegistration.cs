namespace Sankore.Modules.Integration.Infrastructure.CallLog;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The call journal's own registrations (INT-08).
///
/// <para>
/// A slice-local extension rather than lines inside <c>IntegrationModule</c>: several chantiers
/// are building this module in parallel, and a single composition-root file edited by all of them
/// is a merge conflict on every branch. <c>IntegrationModule</c> gains one
/// <c>services.AddCallLogServices()</c> call and never changes again when this slice does.
/// </para>
/// </summary>
internal static class CallLogServiceRegistration
{
    internal static IServiceCollection AddCallLogServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped, like the adapters that consume it and like the DbContext behind it.
        services.AddScoped<ICallJournal, CallJournal>();

        // Scoped too, even though it creates its own child scope per append: it is resolved by a
        // scoped journal, and a singleton here would outlive nothing useful — IServiceScopeFactory
        // is itself a singleton, so the lifetime buys no sharing.
        services.AddScoped<ICallLogStore, CallLogStore>();

        // Transient, as Hangfire activates it per execution. Registering it at all is what lets
        // Hangfire's activator resolve the type from DI with its IntegrationDbContext rather than
        // requiring a public parameterless constructor.
        services.AddTransient<EnsureCallLogPartitionsJob>();

        return services;
    }
}
