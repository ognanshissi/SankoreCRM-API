namespace Sankore.Modules.Integration.Features.Batch.Inbound;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Everything INT-25 (the inbound half of the batch socle) adds to the container. One
/// registration per <c>Features/&lt;Area&gt;/</c> folder, the shape this module already uses, so
/// several chantiers add themselves without meeting in the composition root.
///
/// <para>
/// Two job types and nothing else. The appliers, the reader and the format are static: a line of
/// an acknowledgement file is a function of the file and the database, and a registered service
/// per step would be three more container entries for no seam anybody substitutes.
/// </para>
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>WIRING REQUIRED — four things this slice may not write itself. Without them INT-25 is code
/// nothing ever calls, and every test in <c>Features/Batch/Inbound/</c> still passes.</b>
/// </para>
///
/// <para>
/// <b>1. <c>IntegrationModule.AddIntegrationModule</c></b> — next to the other area
/// registrations:
/// </para>
/// <code>
/// services.AddInboundBatchServices();
/// </code>
///
/// <para>
/// <b>2. <c>src/Bootstrapper/Sankore.Api/Program.cs</c></b>, in the recurring-job block beside
/// <c>"integration-dispatch-orchestrator"</c> — a GLOBAL registration, like every orchestrator in
/// that block, so a new tenant needs no new recurring job:
/// </para>
/// <code>
/// AddOrUpdate&lt;Sankore.Modules.Integration.Features.Batch.Inbound.InboundBatchPollOrchestratorJob&gt;(
///     InboundBatchPollOrchestratorJob.RecurringJobId,       // "integration-inbound-batch-poll"
///     job => job.ExecuteAsync(),
///     InboundBatchPollOrchestratorJob.CronExpression);      // "*/15 * * * *"
/// </code>
///
/// <para>
/// <b>3. The <c>integration-batch</c> queue</b> must be in the host's
/// <c>BackgroundJobServerOptions.Queues</c> — <c>DispatchServiceRegistration</c> carries the
/// exact block. A queue no worker reads swallows every enqueue in silence.
/// </para>
///
/// <para>
/// <b>4. <c>IBatchAckOverdueAlerter</c> and the event record it publishes.</b> Criterion 3's
/// alert has to be an integration event — nobody reads the batch table unprompted — and an
/// integration event belongs in <c>PublicApi/IntegrationEvents.cs</c>, which this slice does not
/// own. The record that file needs, in its own house style:
/// </para>
/// <code>
/// /// &lt;summary&gt;
/// /// An outbound batch file was never acknowledged within the connection's delay, and commands
/// /// are still waiting on it (INT-25). Consumed by M08 to alert the administrator — and that is
/// /// why it is an event rather than a log line: nobody reads the batch table unprompted.
/// ///
/// /// &lt;para&gt;
/// /// Per FILE, not per command: every command of an unanswered file is overdue for the same
/// /// single reason, and &lt;paramref name="PendingCommandCount"/&gt; is what says how bad it is.
/// /// The commands are deliberately left Batched — a late acknowledgement still closes them
/// /// correctly, and a command closed wrongly cannot be un-closed.
/// /// &lt;/para&gt;
/// /// &lt;/summary&gt;
/// public sealed record IntegrationBatchAckOverdueEvent(
///     Guid TenantId,
///     Guid ConnectionId,
///     Guid BatchFileId,
///     string FileName,
///     long SequenceNo,
///     DateTimeOffset SentAt,
///     int AckTimeoutHours,
///     int PendingCommandCount,
///     DateTimeOffset DetectedAt) : IntegrationEventBase;
/// </code>
///
/// <para>
/// and the implementation it needs, which may then live in this folder and be registered in the
/// method below:
/// </para>
/// <code>
/// internal sealed class OutboxBatchAckOverdueAlerter(
///     [FromKeyedServices(nameof(IntegrationDbContext))] IEventPublisher publisher)
///     : IBatchAckOverdueAlerter
/// {
///     // Published as the CONCRETE record type, never through a base-typed variable: the outbox
///     // stores typeof(TEvent).AssemblyQualifiedName. It does NOT save — AckOverdueSweep's single
///     // SaveChanges is what makes the alert and the file's status atomic.
///     public Task AlertAsync(BatchAckOverdueAlert a, CancellationToken ct)
///         => publisher.PublishAsync(
///             new IntegrationBatchAckOverdueEvent(
///                 a.TenantId, a.ConnectionId, a.BatchFileId, a.FileName, a.SequenceNo,
///                 a.SentAt, a.AckTimeoutHours, a.PendingCommandCount, a.DetectedAt), ct);
/// }
/// </code>
///
/// <para>
/// <see cref="PollInboundBatchFilesJob"/> resolves the alerter with <c>GetRequiredService</c>, so
/// until it is registered the sweep fails its first run visibly instead of skipping criterion 3
/// in silence. That is the intended failure.
/// </para>
///
/// <para>
/// <b>Not registered here either: <c>IIntegrationFileTransport</c>.</b> It is the fixed seam this
/// slice consumes and the outbound chantier implements — SFTP directly, or through the relay
/// agent, resolved from the connection's <c>IntegrationMode</c>. The same
/// <c>GetRequiredService</c> rule applies for the same reason.
/// </para>
/// </summary>
internal static class InboundBatchServiceRegistration
{
    internal static IServiceCollection AddInboundBatchServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Closed by the reconciliation rather than by this slice: the event it publishes lives in
        // the PublicApi, which this folder does not own. See OutboxBatchAckOverdueAlerter.
        services.AddScoped<IBatchAckOverdueAlerter, OutboxBatchAckOverdueAlerter>();

        // Transient, like every job type in this solution: Hangfire activates them from the
        // container once per execution, and a singleton would share a captured scope factory's
        // state across concurrent sweeps.
        services.AddTransient<InboundBatchPollOrchestratorJob>();
        services.AddTransient<PollInboundBatchFilesJob>();

        return services;
    }
}
