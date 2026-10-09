namespace Sankore.Modules.Integration.Features.KycLimits;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Everything INT-22 (the simplified-KYC ceiling watch) adds to the container. One registration
/// per <c>Features/&lt;Area&gt;/</c> folder, the shape the module already uses, so this slice adds
/// itself next to itself.
///
/// <para>
/// Nothing but the two job types is registered, and that is the point of the slice: the ceilings,
/// the flow window and the alert percentage are tenant settings of M02
/// (<c>simplified-max-balance</c>, <c>simplified-max-monthly-flow</c>,
/// <c>simplified-flow-window-days</c>, <c>simplified-alert-pct</c>) read through
/// <c>IKycModule.GetLimitsAsync</c>. There is deliberately no options class, no configuration
/// section and no default amount here — a second place to configure a compliance ceiling is a
/// second place for it to be wrong.
/// </para>
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>WIRING REQUIRED — two lines this slice may not write itself.</b> Without them the watch is
/// code nothing ever calls, and every test in <c>Features/KycLimits/</c> still passes.
/// </para>
///
/// <para>
/// <b>1. <c>IntegrationModule.AddIntegrationModule</c></b> — next to the other area
/// registrations:
/// </para>
/// <code>
/// services.AddKycLimitsServices();
/// </code>
///
/// <para>
/// <b>2. <c>src/Bootstrapper/Sankore.Api/Program.cs</c></b>, in the recurring-job block beside
/// <c>"integration-dispatch-orchestrator"</c> — a GLOBAL registration, like every orchestrator in
/// that block, so a new tenant needs no new recurring job:
/// </para>
/// <code>
/// AddOrUpdate&lt;Sankore.Modules.Integration.Features.KycLimits.KycLimitWatchOrchestratorJob&gt;(
///     KycLimitWatchOrchestratorJob.RecurringJobId,          // "integration-kyc-limit-watch"
///     job => job.ExecuteAsync(),
///     KycLimitWatchOrchestratorJob.CronExpression);         // "0 5 * * *"
/// </code>
///
/// <para>
/// The <c>integration-sync</c> queue <see cref="KycLimitWatchJob"/> runs on is already in the
/// <c>BackgroundJobServerOptions</c> that <c>DispatchServiceRegistration</c> specifies, so no
/// third change is needed — but if that host wiring was never applied, this job is enqueued to a
/// queue no worker reads and fails silently along with the dispatcher.
/// </para>
/// </summary>
internal static class KycLimitsServiceRegistration
{
    internal static IServiceCollection AddKycLimitsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Transient, like every job type in this solution: Hangfire activates them from the
        // container once per execution, and a singleton would share a captured scope factory's
        // state across concurrent watches.
        services.AddTransient<KycLimitWatchOrchestratorJob>();
        services.AddTransient<KycLimitWatchJob>();

        return services;
    }
}
