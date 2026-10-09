namespace Sankore.Modules.Integration.Features.Batch.Outbound;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Features.Commands.Execute;
using Sankore.Modules.Integration.Infrastructure.BatchStorage;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// Everything INT-24 (the outbound batch socle) adds to the container.
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>HOST WIRING REQUIRED.</b>
/// </para>
///
/// <para>
/// <b>1. Call this from <c>IntegrationModule.AddIntegrationModule</c>, BEFORE
/// <c>AddCommandsServices()</c>:</b>
/// </para>
/// <code>
/// services.AddOutboundBatchServices();   // INT-24 — must precede AddCommandsServices
/// services.AddCommandsServices();
/// </code>
/// <para>
/// <c>AddCommandsServices</c> registers <c>UnavailableBatchFileEnlister</c> with <c>TryAdd</c>,
/// whose own comment says INT-24's writer is expected to be registered first. Called the other
/// way round, <see cref="OutboundBatchFileEnlister"/> still wins (the last registration of a
/// service is the one resolved) — but the <c>TryAdd</c> then silently does nothing while reading
/// as though it were the active default, which is the kind of ambiguity that survives a
/// refactor. Register it first and both readings agree.
/// </para>
///
/// <para>
/// <b>2. The recurring orchestrator.</b> In <c>Program.cs</c>, inside the existing
/// <c>if (emitOpenApiTo is null)</c> block, next to the other GLOBAL orchestrators and
/// <b>outside</b> the <c>foreach (var t in activeTenants)</c> loop — this job walks the tenants
/// itself:
/// </para>
/// <code>
/// // ── Integration module (INT-24) ───────────────────────────────────────────
/// AddOrUpdate&lt;Sankore.Modules.Integration.Features.Batch.Outbound.OutboundBatchOrchestratorJob&gt;(
///     Sankore.Modules.Integration.Features.Batch.Outbound.OutboundBatchOrchestratorJob.RecurringJobId,
///     job =&gt; job.ExecuteAsync(),
///     Sankore.Modules.Integration.Features.Batch.Outbound.OutboundBatchOrchestratorJob.CronExpression);
/// </code>
/// <para>
/// Through the local <c>AddOrUpdate</c> helper, not <c>RecurringJob.AddOrUpdate</c> directly, so a
/// sweep an operator paused from the dashboard's Job control page is not resurrected on the next
/// boot.
/// </para>
///
/// <para>
/// <b>3. The queue.</b> <c>integration-batch</c> is already in
/// <c>DispatchServiceRegistration.Queues</c> and in that class's host-wiring instructions, so no
/// new queue is needed — but the <c>BackgroundJobServerOptions</c> it describes must actually be
/// declared. <b>Without it, <see cref="GenerateTenantOutboundBatchFilesJob"/> is enqueued to a
/// queue no worker reads and never runs</b>, while every test here still passes. That class's
/// note about splitting the server once "a single SFTP transfer can hold a worker for minutes" is
/// now live: INT-24 has landed.
/// </para>
///
/// <para>
/// <b>4. The object-storage concern</b> is <c>IntegrationBatchStorage.Concern</c>
/// (<c>"integration-batch-files"</c>). Add it to the host's concern list so a bucket deployment
/// registers an R2 backend under that key BEFORE this method runs; the filesystem fallback below
/// is a <c>TryAdd</c> and then does nothing. The module must not choose the medium — that would
/// mean referencing the S3 client from an assembly that hosts storing no object also load.
/// </para>
///
/// <para>
/// <b>5. Settings.</b> <c>Integration:Batch:Storage:EncryptionKey</c> is required for a
/// deployment that exchanges files (base64, 32 bytes — a THIRD key, distinct from
/// <c>Integration:FieldEncryptionKey</c>); <c>Integration:Batch:Storage:BasePath</c> should point
/// at a mounted volume when no bucket is configured. Both are read on the first generation rather
/// than at boot, so a deployment with no batch connection needs neither.
/// <c>Integration:Egress:AllowPrivateAddresses</c> is <b>false</b> by default and must stay so on
/// any hosted deployment.
/// </para>
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </para>
/// </summary>
internal static class OutboundBatchServiceRegistration
{
    internal static IServiceCollection AddOutboundBatchServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ── Options ─────────────────────────────────────────────────────────
        // BindConfiguration rather than a passed IConfiguration: this method takes only the
        // collection, and the binding is resolved from the container at build time — the same
        // device SyncServiceRegistration and AddSankoreFileStore already use.
        services.AddOptions<IntegrationBatchStorageOptions>()
            .BindConfiguration(IntegrationBatchStorageOptions.SectionName);

        // BasePath is resolved against the content root, which only the host can see and an
        // IOptions binding cannot. PostConfigure is where that becomes possible.
        services.AddOptions<IntegrationBatchStorageOptions>()
            .PostConfigure<IHostEnvironment>((opts, env) =>
            {
                if (string.IsNullOrWhiteSpace(opts.BasePath))
                    opts.BasePath = Path.Combine(env.ContentRootPath, "integration-batch-files");
            });

        services.AddOptions<IntegrationEgressOptions>()
            .BindConfiguration(IntegrationEgressOptions.SectionName);

        // Says out loud, once, at boot, when the egress guard has been loosened. Logs nothing in
        // the default configuration.
        services.AddHostedService<IntegrationEgressStartupWarning>();

        // ── Storage ─────────────────────────────────────────────────────────
        // The medium, keyed to this concern so it cannot steal — or be stolen by — the KYC
        // documents' backend. TryAdd, so this is only the FALLBACK: a host that configures a
        // bucket registers an R2 backend under the same key first and this does nothing.
        services.TryAddKeyedSingleton<IObjectBackend>(IntegrationBatchStorage.Concern, (sp, _) =>
        {
            var options = sp.GetRequiredService<IOptions<IntegrationBatchStorageOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<LocalObjectBackend>>();

            return new LocalObjectBackend(
                IntegrationBatchStorageOptions.ResolveBasePath(options, logger), logger);
        });

        // Resolved from a factory, so its eager key read happens on the first generation rather
        // than at boot — see EncryptedBatchFileStore's constructor.
        services.TryAddSingleton<IBatchFileStore>(sp => new EncryptedBatchFileStore(
            sp.GetRequiredKeyedService<IObjectBackend>(IntegrationBatchStorage.Concern),
            sp.GetRequiredService<IOptions<IntegrationBatchStorageOptions>>(),
            sp.GetRequiredService<ILogger<EncryptedBatchFileStore>>()));

        // ── Transport ───────────────────────────────────────────────────────
        // Both concrete transports are registered, and the ROUTER is what satisfies
        // IIntegrationFileTransport — so no caller can pick a carrier, which is the seam's whole
        // point. Scoped because the relay one reads through the DbContext.
        services.AddScoped<SftpEgressGuard>();
        services.AddScoped<SftpFileTransport>();
        services.AddScoped<RelayFileTransport>();
        services.TryAddScoped<IIntegrationFileTransport, IntegrationFileTransportRouter>();

        // ── Format ──────────────────────────────────────────────────────────
        // TryAdd: the format belongs to the adapter (criterion 2), so an adapter assembly that
        // knows its CBS's real layout replaces this fallback. None does today — the batch-capable
        // kinds are all blocked on their interface specifications.
        services.TryAddSingleton<IOutboundBatchFormatter, DelimitedOutboundBatchFormatter>();

        // ── Generation and the dispatcher's seam ────────────────────────────
        services.AddScoped<OutboundBatchFileGenerator>();

        // NOT TryAdd. This is INT-24's real writer and it must replace
        // UnavailableBatchFileEnlister, whose only job is to say that a deployment has none.
        services.AddScoped<IBatchFileEnlister, OutboundBatchFileEnlister>();

        // ── Jobs ────────────────────────────────────────────────────────────
        // Transient, like every job type in this solution: Hangfire activates them from the
        // container once per execution, and a singleton would share a captured scope factory's
        // state across concurrent passes.
        services.AddTransient<OutboundBatchOrchestratorJob>();
        services.AddTransient<GenerateTenantOutboundBatchFilesJob>();

        return services;
    }
}
