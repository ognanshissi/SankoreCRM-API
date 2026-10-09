namespace Sankore.Modules.Integration.Features.Sync;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Everything INT-20 (incremental synchronisation) adds to the container.
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>WIRING REQUIRED — four lines, none of them in this folder. INT-20 is inert without all
/// four, and every test in the module still passes.</b>
/// </para>
///
/// <para>
/// <b>1. The module composition — <c>IntegrationModule.AddIntegrationModule</c>.</b> Next to the
/// other per-area registrations:
/// </para>
/// <code>
/// services.AddSyncServices();
/// </code>
///
/// <para>
/// <b>2. The public webhook — <c>src/Bootstrapper/Sankore.Api/Program.cs</c>.</b> On the ROOT
/// application, outside <c>api/v1</c> and before <c>app.UseAuthentication()</c>, where M13's
/// public ingest endpoints are mapped:
/// </para>
/// <code>
/// app.MapIntegrationWebhookEndpoints();
/// </code>
///
/// <para>
/// <b>3. The recurring orchestrator — <c>Program.cs</c>.</b> Inside the existing
/// <c>if (emitOpenApiTo is null)</c> block (the one that builds <c>pauseStore</c> and declares the
/// local <c>AddOrUpdate</c>), next to the other GLOBAL orchestrators and <b>outside</b> the
/// <c>foreach (var t in activeTenants)</c> loop — this job walks the tenants itself:
/// </para>
/// <code>
/// // ── Integration module (INT-20) ───────────────────────────────────────────
/// // GLOBAL: it enumerates the active tenants itself and enqueues one job per (active
/// // connection, due stream) on "integration-sync", so a new tenant and a new connection
/// // both need no new registration. Every minute is the RESOLUTION of the schedule, not the
/// // sweep period — the period is per stream and per tenant, in the connection's settings.
/// AddOrUpdate&lt;Sankore.Modules.Integration.Features.Sync.IntegrationSyncOrchestrator&gt;(
///     Sankore.Modules.Integration.Features.Sync.IntegrationSyncOrchestrator.RecurringJobId,
///     job =&gt; job.ExecuteAsync(),
///     Sankore.Modules.Integration.Features.Sync.IntegrationSyncOrchestrator.CronExpression);
/// </code>
/// <para>
/// Use the local <c>AddOrUpdate</c> helper rather than <c>RecurringJob.AddOrUpdate</c> directly,
/// so a sweep an operator paused from the dashboard's Job control page is not resurrected on the
/// next boot.
/// </para>
///
/// <para>
/// <b>4. The <c>integration-sync</c> queue must have a worker.</b> Already named in
/// <see cref="Dispatch.DispatchServiceRegistration"/>'s <c>BackgroundJobServerOptions</c> block —
/// INT-20 adds no new queue, it is the second occupant of that one. If that block has not yet been
/// applied to <c>Program.cs</c>, applying it is part of this wiring: <c>AddHangfireServer()</c>
/// with no options serves only <c>"default"</c>, so both sync jobs would sit enqueued for ever
/// with nothing in the logs.
/// </para>
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </para>
///
/// <para>
/// <b>Two things this slice needs from elsewhere and does not provide.</b>
/// </para>
/// <para>
/// <see cref="ICbsSnapshotProjector"/> is INT-21's and is <b>not registered here</b>: INT-20 must
/// not write the snapshot. Both jobs resolve it with <c>GetRequiredService</c>, so until INT-21
/// lands the first run throws and names the missing service — which is the intended failure, as
/// against sweeping every customer and projecting nothing.
/// </para>
/// <para>
/// The webhook reuses the host's existing <c>"ingest-key"</c> rate-limiting policy, which
/// partitions on remote IP plus the route value <c>publicKey</c>. This route has no
/// <c>publicKey</c>, so every integration webhook arriving from one sender shares a single window
/// of 10 requests a minute. That is a deliberate reuse rather than an invented policy name — a name
/// the host does not declare means no rate limiting at all, silently — but a back office that
/// emits one notification per customer event will be throttled, and widening it is a host
/// decision: either add <c>connectionId</c> to that policy's partition key, or give the route its
/// own policy.
/// </para>
/// </summary>
internal static class SyncServiceRegistration
{
    internal static IServiceCollection AddSyncServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ── Criterion 4 — the anti-replay window, validated at start-up ─────
        // A non-positive window would refuse every webhook ever sent (fail closed, but
        // indistinguishable from a wrong secret), and an hour-plus window makes the anti-replay
        // guarantee decorative: a captured request would stay replayable for the rest of the
        // working day. Both are the kind of mistake that looks like a working deployment, so the
        // boot fails instead.
        services.AddOptions<IntegrationWebhookOptions>()
            .BindConfiguration(IntegrationWebhookOptions.SectionName)
            .Validate(
                o => o.ReplayWindowMinutes is > 0 and <= 60,
                $"{IntegrationWebhookOptions.SectionName}:ReplayWindowMinutes must be between 1 "
                + "and 60 (default 5). Zero refuses every webhook; a wide window leaves a "
                + "captured request replayable long after it was minted.")
            .ValidateOnStart();

        // ── The jobs ────────────────────────────────────────────────────────
        // Transient, like every job type in this solution: Hangfire activates them from the
        // container once per execution, and a singleton would share a captured scope factory's
        // state across concurrent sweeps.
        services.AddTransient<IntegrationSyncOrchestrator>();
        services.AddTransient<IntegrationSyncJob>();
        services.AddTransient<SyncCustomerJob>();

        return services;
    }
}
