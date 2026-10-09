namespace Sankore.Modules.Integration.Features.Dispatch;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Infrastructure.Resilience;

/// <summary>
/// Everything INT-06 (the dispatcher) and INT-09 (resilience) add to the container. Called from
/// <c>IntegrationModule.AddIntegrationModule</c> with one line, so neither slice has to edit that
/// file.
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>HOST WIRING REQUIRED — <c>src/Bootstrapper/Sankore.Api/Program.cs</c>. This is not optional
/// and it is not a suggestion: without it the dispatcher is enqueued to queues no worker reads,
/// and INT-06 criterion 5 and INT-09 criterion 3 are both unmet while every test still passes.</b>
/// </para>
///
/// <para>
/// <b>1. Named queues with dedicated workers.</b> Line 320 today reads
/// <c>if (emitOpenApiTo is null) builder.Services.AddHangfireServer();</c> — no options at all,
/// which means one worker pool on the single queue <c>"default"</c>. There is no named queue
/// anywhere in this repository yet, so this is the first. Replace that call with:
/// </para>
/// <code>
/// if (emitOpenApiTo is null)
/// {
///     builder.Services.AddHangfireServer(options =>
///     {
///         // Order IS priority: Hangfire's workers drain queues left to right, so a queue listed
///         // first starves the ones after it when it is busy. "default" stays FIRST because it
///         // carries the interactive work — a lead import an agent is watching, a user import, a
///         // KYC verification replay — and must never wait behind a batch file.
///         options.Queues =
///         [
///             "default",              // every existing job in the solution
///             "integration-write",    // DispatchTenantCommandsJob (INT-06)
///             "integration-sync",     // INT-20/21 incremental sync and snapshot refresh
///             "integration-batch",    // INT-24/25 outbound file production and ack ingestion
///         ];
///
///         // One pool serves all four queues, so it must be big enough for the slowest. 20 is
///         // Hangfire's own default formula (5 × processor count) floored for a small container;
///         // the dispatch sweep holds a worker for the length of a CBS call, and a tenant whose
///         // bank answers in eight seconds must not be able to occupy every worker there is.
///         options.WorkerCount = Math.Max(20, Environment.ProcessorCount * 5);
///     });
/// }
/// </code>
/// <para>
/// <b><c>"default"</c> MUST stay in that list.</b> Setting <c>Queues</c> replaces the default
/// rather than adding to it: omit it and every recurring and on-demand job already in this
/// solution — the SLA sweep, nurturing, lead and user imports, the pull orchestrator, M01's
/// nightly jobs, the KYC review orchestrator — stops being processed. Nothing fails; the jobs
/// simply sit in <c>default</c> for ever while the dashboard shows them enqueued.
/// </para>
///
/// <para>
/// <b>Why one pool and not three servers.</b> <c>AddHangfireServer</c> may be called several
/// times to give each queue its own worker count, and that is the right shape once the batch
/// socle (INT-24/25) exists and a single SFTP transfer can hold a worker for minutes. Until then
/// three servers triple the heartbeat and lock traffic against the same Postgres for no
/// isolation anyone can observe. Split when INT-24 lands, not before.
/// </para>
///
/// <para>
/// <b>2. The recurring orchestrator.</b> Inside the existing
/// <c>if (emitOpenApiTo is null)</c> block in <c>Program.cs</c> (the one that builds
/// <c>pauseStore</c> and declares the local <c>AddOrUpdate</c>), next to the other GLOBAL
/// orchestrators and <b>outside</b> the <c>foreach (var t in activeTenants)</c> loop — this job
/// walks the tenants itself:
/// </para>
/// <code>
/// // ── Integration module (INT-06) ───────────────────────────────────────────
/// // GLOBAL like M01's and M02's: it enumerates the active tenants itself and enqueues one
/// // opaque per-tenant job on "integration-write", so a new tenant needs no new registration.
/// AddOrUpdate&lt;Sankore.Modules.Integration.Features.Dispatch.IntegrationDispatchOrchestratorJob&gt;(
///     Sankore.Modules.Integration.Features.Dispatch.IntegrationDispatchOrchestratorJob.RecurringJobId,
///     job =&gt; job.ExecuteAsync(),
///     Sankore.Modules.Integration.Features.Dispatch.IntegrationDispatchOrchestratorJob.CronExpression);
/// </code>
/// <para>
/// Use the local <c>AddOrUpdate</c> helper rather than <c>RecurringJob.AddOrUpdate</c> directly,
/// so a sweep an operator paused from the dashboard's Job control page is not resurrected on the
/// next boot.
/// </para>
///
/// <para>
/// <b>3. The dashboard host.</b> <c>src/Bootstrapper/Sankore.Hangfire</c> needs a
/// <c>ProjectReference</c> to this module for the two job types to display with their real names
/// instead of a serialised type string. It registers no services and runs no server, exactly as
/// it does for Leads and Administration today.
/// </para>
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </para>
/// </summary>
internal static class DispatchServiceRegistration
{
    /// <summary>The three queues this module owns. Named here so the host has one source.</summary>
    public static readonly string[] Queues =
        [DispatchTenantCommandsJob.QueueName, "integration-sync", "integration-batch"];

    internal static IServiceCollection AddDispatchServices(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // ── INT-06 — the attempt budget, validated at start-up ──────────────
        // A non-positive value fails the boot rather than being silently corrected: a MaxAttempts
        // of 0 would reject every command on its first transient failure, and a CapSeconds of 0
        // would turn the backoff into a tight loop against a bank. Both are the kind of mistake
        // that looks like a working deployment.
        services.AddOptions<IntegrationRetryOptions>()
            .Bind(config.GetSection(IntegrationRetryOptions.SectionName))
            .Validate(
                o => o.MaxAttempts > 0,
                $"{IntegrationRetryOptions.SectionName}:MaxAttempts must be a positive integer "
                + "(default 8). Zero would reject every command on its first transient failure.")
            .Validate(
                o => o.CapSeconds > 0,
                $"{IntegrationRetryOptions.SectionName}:CapSeconds must be a positive integer "
                + "(default 3600). Zero would retry without any delay at all.")
            .Validate(
                o => o.BaseSeconds > 0,
                $"{IntegrationRetryOptions.SectionName}:BaseSeconds must be a positive integer "
                + "(default 30). Zero would make every delay in the curve zero.")
            .Validate(
                o => o.JitterFraction is >= 0 and < 1,
                $"{IntegrationRetryOptions.SectionName}:JitterFraction must be in [0, 1) "
                + "(default 0.2). At 1 a delay could be shaved away entirely.")
            .ValidateOnStart();

        // The contract lives in Features/Commands (next to the handler that consumes it) and the
        // implementation here, because the curve is the dispatcher's business: it is the scan
        // cadence that decides what a "due" command costs. Singleton — the options are read once
        // and the object holds nothing per request.
        services.AddSingleton<ICommandRetryPolicy, ExponentialCommandRetryPolicy>();

        // ── INT-06 — the jobs ───────────────────────────────────────────────
        // Transient, like every job type in this solution: Hangfire activates them from the
        // container once per execution, and a singleton would share a captured scope factory's
        // state across concurrent sweeps.
        services.AddTransient<IntegrationDispatchOrchestratorJob>();
        services.AddTransient<DispatchTenantCommandsJob>();

        // ── INT-09 — resilience ─────────────────────────────────────────────
        // Both singletons, and both for the same reason: their state IS the feature. The pipeline
        // provider holds one circuit-breaker state provider per connection, which the health check
        // reads back; the rate limiter holds the per-minute windows. Scoped, each would hand every
        // request a fresh breaker and an empty allowance — observable as a breaker that never opens
        // and a limit that never bites.
        services.AddSingleton<IntegrationResiliencePipelineProvider>();
        services.AddSingleton<TenantRateLimiter>();

        // ── INT-09 criterion 4 — the breaker state in the module's health check ──
        // The first named check in this repository: ServiceDefaults calls AddHealthChecks() and
        // nothing has contributed to it until now. See IntegrationHealthCheck for the second half
        // of that story — the detailed /health endpoint is Development-gated in
        // Sankore.Api/Infrastructure/ServiceDefaults.cs, so a bare production /health is not
        // evidence that this check is absent.
        services.AddHealthChecks()
            .AddCheck<IntegrationHealthCheck>("integration", tags: ["integration"]);

        return services;
    }
}
