namespace Sankore.Modules.Integration.Features.Reconciliation;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Everything INT-34 (the daily reconciliation) adds to the container. One registration per
/// <c>Features/&lt;Area&gt;/</c> folder, the shape this module already uses, so this slice adds
/// itself next to itself.
///
/// <para>
/// Only the two job types. There is deliberately no options class and no configuration section:
/// the comparison has nothing to tune — its sources are the reference table and the INT-21 read
/// model, its schedule is one cron in the host, and a configurable gap definition would be a
/// second place for a compliance rule to be wrong. The handlers and validators are discovered by
/// the module's assembly scans (<c>AddMediatR</c>, and <c>AddValidatorsFromAssembly</c> with
/// <c>includeInternalTypes</c> — this slice's validator is <c>internal sealed</c> like the rest).
/// </para>
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>WIRING REQUIRED — one line this slice may not write itself.</b> Without it the comparison is
/// code nothing ever calls, and every test in <c>Features/Reconciliation/</c> still passes.
/// </para>
///
/// <para>
/// <c>src/Bootstrapper/Sankore.Api/Program.cs</c>, in the recurring-job block beside the other
/// five Integration orchestrators and <b>outside</b> the <c>foreach (var t in activeTenants)</c>
/// loop — a GLOBAL registration, because this job walks the tenants itself:
/// </para>
/// <code>
/// AddOrUpdate&lt;Sankore.Modules.Integration.Features.Reconciliation.ReconciliationOrchestratorJob&gt;(
///     Sankore.Modules.Integration.Features.Reconciliation.ReconciliationOrchestratorJob.RecurringJobId,
///     job =&gt; job.ExecuteAsync(),
///     Sankore.Modules.Integration.Features.Reconciliation.ReconciliationOrchestratorJob.CronExpression);
/// </code>
///
/// <para>
/// Use the local <c>AddOrUpdate</c> helper rather than <c>RecurringJob.AddOrUpdate</c> directly,
/// so a comparison an operator paused from the dashboard's Job control page is not resurrected on
/// the next boot.
/// </para>
///
/// <para>
/// The <c>integration-sync</c> queue <see cref="ReconcileTenantJob"/> runs on is already in the
/// <c>BackgroundJobServerOptions.Queues</c> the host specifies, so no second change is needed —
/// but note that setting <c>Queues</c> REPLACES the default set: a per-tenant job whose queue name
/// is absent from that list is enqueued successfully and never processed, with nothing in the logs.
/// </para>
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </para>
/// </summary>
internal static class ReconciliationServiceRegistration
{
    internal static IServiceCollection AddReconciliationServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Transient, like every job type in this solution: Hangfire activates them from the
        // container once per execution, and a singleton would share a captured scope factory's
        // state across concurrent comparisons.
        services.AddTransient<ReconciliationOrchestratorJob>();
        services.AddTransient<ReconcileTenantJob>();

        return services;
    }
}
