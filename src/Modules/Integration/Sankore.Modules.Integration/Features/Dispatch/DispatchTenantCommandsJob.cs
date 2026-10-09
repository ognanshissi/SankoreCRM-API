namespace Sankore.Modules.Integration.Features.Dispatch;

using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Features.Commands.Execute;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job — the dispatch sweep of ONE tenant (INT-06). Enqueued by
/// <see cref="IntegrationDispatchOrchestratorJob"/>; its only argument is an opaque tenant
/// identifier (criterion 2).
///
/// <para>
/// <b>It sends and it does not execute.</b> The whole body of work for one command is
/// <c>ExecuteIntegrationCommandCommand(commandId, tenantId)</c> through MediatR — the claim, the
/// adapter call, the call log, the reference row and the retry decision all happen inside that
/// handler's pipeline, under <c>TransactionBehavior</c> and <c>AuditBehavior</c>. A job that
/// called an adapter itself would be a second, un-audited, un-transacted path to the same write.
/// </para>
///
/// <para>
/// <b>On the named queue <c>integration-write</c></b> (criterion 5). A dispatch sweep makes
/// outbound calls to a bank with a per-minute licence ceiling; it must not share a worker pool
/// with a four-hundred-row lead import, and an operator must be able to drain it on its own.
/// <see cref="DispatchServiceRegistration"/> carries the exact <c>BackgroundJobServerOptions</c>
/// the host has to declare for this attribute to mean anything — <b>without it the job is enqueued
/// to a queue no worker reads and never runs at all</b>.
/// </para>
/// </summary>
[Queue(QueueName)]
public sealed class DispatchTenantCommandsJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// The write queue. Also referenced by <see cref="DispatchServiceRegistration"/>'s host-wiring
    /// instructions, so the name is declared once.
    /// </summary>
    public const string QueueName = "integration-write";

    public async Task ExecuteAsync(Guid tenantId)
    {
        // Set BEFORE the scope is created. ICurrentUser and ITenantContext are built from this
        // ambient context when the scope resolves them, and a scope opened first would capture the
        // HTTP implementations and find no request — the precedent, with the same comment, is
        // ProcessTenantKycReviewsJob. The actor is SYSTEM: nobody decided that a command was due,
        // a timestamp did, and the audit rows the handler writes must say so rather than name
        // whichever agent happened to create the command hours earlier.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<DispatchTenantCommandsJob>>();

        var report = await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<ISender>(),
            sp.GetRequiredService<ICommandRetryPolicy>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            CancellationToken.None);

        logger.LogInformation(
            "Integration dispatch for tenant {TenantId}: {Attempted} attempted, {Succeeded} "
            + "sent, {Rescheduled} rescheduled, {Rejected} rejected, {GroupsHalted} entity "
            + "queue(s) halted, {Recovered} recovered from a faulted attempt.",
            tenantId, report.Attempted, report.Succeeded, report.Rescheduled, report.Rejected,
            report.GroupsHalted, report.Recovered);
    }

    /// <summary>
    /// The sweep itself, with its collaborators passed in rather than resolved.
    ///
    /// <para>
    /// Separated from <see cref="ExecuteAsync"/> so the ordering guarantee below can be pinned by
    /// a test without standing up a DI container and a Hangfire storage — the scope handling is
    /// the only thing <see cref="ExecuteAsync"/> adds, and it is identical in every job of the
    /// repo. Same split as <c>ProcessTenantKycReviewsJob.RunAsync</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Criterion 3 — one customer's commands execute in creation order.</b> The due set is
    /// grouped by <c>(EntityType, CrmId)</c> — a customer, an account, a policy — and each group
    /// runs sequentially in <c>CreatedAt</c> then <c>Id</c> order. <c>CreatedAt</c> alone is not a
    /// total order: two commands created in the same transaction share a timestamp to the tick,
    /// and the index <c>ix_integration_command_tenant_entity_created</c> would then hand them back
    /// in whatever order the heap pleases. <c>Id</c> breaks the tie deterministically.
    /// </para>
    ///
    /// <para>
    /// The order is load-bearing, not tidiness: <c>UpdateCustomer</c> reaching a CBS before the
    /// <c>CreateCustomer</c> it amends fails as "entity not found", and <c>ReverseDebit</c> before
    /// its <c>DebitAccount</c> would reverse nothing while the debit later lands unreversed. So
    /// <b>a group stops at its first command that did not leave the platform</b> — rescheduled,
    /// rejected, claimed by another worker or faulted alike — and its remaining commands wait for
    /// the next sweep. Nothing overtakes a command that has not got out.
    /// </para>
    ///
    /// <para>
    /// <b>Groups run sequentially, one after another, within the tenant's job.</b> Deliberate: the
    /// ordering guarantee is only ever intra-group, so parallelism across groups would be correct
    /// — but the tenant's outbound allowance (INT-09's <c>RateLimitPerMinute</c>, a number the CBS
    /// licence fixes) is shared by the whole group set, and a parallel fan-out would spend it in a
    /// burst and then refuse its own calls with <c>INTEGRATION_RATE_LIMITED</c>. Parallelism is put
    /// where it costs nothing instead: one job per tenant, several workers on the
    /// <c>integration-write</c> queue, so tenants proceed concurrently and a slow CBS at one IMF
    /// never holds up another.
    /// </para>
    /// </summary>
    internal static async Task<DispatchSweepReport> RunAsync(
        IntegrationDbContext db,
        ISender sender,
        ICommandRetryPolicy retryPolicy,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var report = new DispatchSweepReport();

        // Identifiers and ordering keys only. The payload column is encrypted and has no business
        // travelling into this job's memory: the dispatcher never reads what it sends.
        // IgnoreQueryFilters paired with an explicit tenant predicate — a job runs outside any
        // HTTP request, so the ambient tenant is not necessarily the one being swept. The
        // predicate MIRRORS IntegrationCommand.IsDueAt, which an instance method cannot do in
        // SQL; the two must keep agreeing.
        var due = await db.Commands
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && (c.Status == CommandStatus.Pending
                         || (c.Status == CommandStatus.RetryScheduled
                             && (c.NextAttemptAt == null || c.NextAttemptAt <= now))
                         // A claim left behind by a worker that died mid-call. Without this
                         // branch the command stays Sending for ever and the write is owed with
                         // nothing anywhere to say so. The expiry check is what keeps a live
                         // worker's command from being taken over and sent twice.
                         || (c.Status == CommandStatus.Sending
                             && c.NextAttemptAt != null && c.NextAttemptAt <= now)))
            .OrderBy(c => c.EntityType)
            .ThenBy(c => c.CrmId)
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Select(c => new DueCommand(c.Id, c.EntityType, c.CrmId, c.CreatedAt))
            .ToListAsync(ct);

        foreach (var group in due.GroupBy(c => (c.EntityType, c.CrmId)))
        {
            // Re-ordered here as well as in SQL: GroupBy preserves the source order, but stating
            // the order where the guarantee lives means a future change to the query cannot
            // silently break it.
            foreach (var command in group.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id))
            {
                report.Attempted++;

                var mayContinue = await ExecuteOneAsync(
                    db, sender, retryPolicy, clock, logger, tenantId, command.Id, report, ct);

                if (mayContinue) continue;

                report.GroupsHalted++;

                logger.LogDebug(
                    "Integration dispatch: {EntityType} {CrmId} halted at command {CommandId}; "
                    + "its remaining commands wait for the next sweep.",
                    command.EntityType, command.CrmId, command.Id);

                break;
            }
        }

        return report;
    }

    /// <summary>
    /// One command, start to finish. Returns whether the entity's queue may carry on.
    ///
    /// <para>
    /// <b>A rejection arrives as a SUCCESSFUL result.</b> <c>ExecuteIntegrationCommandHandler</c>
    /// returns <c>Result.Ok</c> for every outcome it managed to record, because
    /// <c>TransactionBehavior</c> commits only on a successful <c>Result</c> and a
    /// <c>Result.Fail</c> would roll back the very row that records the rejection. So the verdict
    /// is read from <see cref="ExecuteIntegrationCommandResult.Status"/>, not from
    /// <c>IsSuccess</c>: branching on <c>IsSuccess</c> alone would let an <c>UpdateCustomer</c>
    /// sail past the <c>CreateCustomer</c> the CBS had just refused.
    /// </para>
    ///
    /// <para>
    /// The handler also owns the per-command retry decision — it holds the same
    /// <see cref="ICommandRetryPolicy"/> and applies <c>ScheduleRetry</c> or <c>Reject</c> itself.
    /// This job therefore does not second-guess it; it only counts, and recovers the one case the
    /// handler structurally cannot: see <see cref="RecoverFaultedAttemptAsync"/>.
    /// </para>
    /// </summary>
    private static async Task<bool> ExecuteOneAsync(
        IntegrationDbContext db,
        ISender sender,
        ICommandRetryPolicy retryPolicy,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid commandId,
        DispatchSweepReport report,
        CancellationToken ct)
    {
        try
        {
            var result = await sender.Send(
                new ExecuteIntegrationCommandCommand(commandId, tenantId), ct);

            if (result.IsFailure)
            {
                // The handler's only genuine failure is CommandNotFound: the row vanished, or
                // belongs to another tenant, between the scan and the call. Halting the group is
                // the conservative read — a command we cannot account for is not a command we can
                // declare "got out" — and the next sweep re-reads committed state.
                logger.LogWarning(
                    "Integration command {CommandId} of tenant {TenantId} could not be executed: "
                    + "{Error}.", commandId, tenantId, result.Error);

                return false;
            }

            return Count(result.Value, report, logger, commandId);
        }
        catch (Exception ex)
        {
            report.Faulted++;
            logger.LogError(
                ex, "Dispatching integration command {CommandId} of tenant {TenantId} threw.",
                commandId, tenantId);

            // The handler's unit of work is half-applied and must not ride along into the recovery
            // write below, which is its own transaction.
            db.ChangeTracker.Clear();

            try
            {
                await RecoverFaultedAttemptAsync(
                    db, retryPolicy, clock, logger, tenantId, commandId, ex, report, ct);
            }
            catch (Exception recoveryEx)
            {
                // The store itself is refusing writes. Nothing useful is left to do for this
                // command; it stays claimed and the NEXT sweep's recovery pass finds it.
                logger.LogError(
                    recoveryEx,
                    "Recovering faulted integration command {CommandId} also failed.", commandId);
            }

            return false;
        }
    }

    /// <summary>
    /// Reads the handler's verdict into the report and decides whether the entity's queue may
    /// carry on. Nothing is written here — the handler already committed the status.
    /// </summary>
    private static bool Count(
        ExecuteIntegrationCommandResult outcome,
        DispatchSweepReport report,
        ILogger logger,
        Guid commandId)
    {
        if (!Enum.TryParse<CommandStatus>(outcome.Status, out var status))
        {
            logger.LogWarning(
                "Integration command {CommandId} reported an unrecognised status {Status}.",
                commandId, outcome.Status);

            return false;
        }

        switch (status)
        {
            case CommandStatus.Succeeded:
                report.Succeeded++;
                return true;

            // The command left through an outbound file and is waiting for an acknowledgement
            // (INT-24/25). The send happened and the file preserves the order within itself, so
            // the entity's next command may join the same file — blocking here would mean one
            // command per customer per batch cycle.
            case CommandStatus.Batched:
                report.Succeeded++;
                return true;

            case CommandStatus.RetryScheduled:
                report.Rescheduled++;
                return false;

            case CommandStatus.Rejected:
                report.Rejected++;
                return false;

            // Sending: another worker won the claim on xmin and is calling right now. Its command
            // has not finished, so nothing of this entity's may follow it.
            default:
                return false;
        }
    }

    /// <summary>
    /// The one gap the handler cannot close itself: an attempt that <b>threw</b>.
    ///
    /// <para>
    /// The handler claims the command with <c>BeginSending</c> and saves that claim immediately,
    /// before any call — which is what stops two workers calling a CBS twice. If the attempt then
    /// throws (a validator, a serialisation fault, a dropped connection) the ambient transaction
    /// rolls back everything after the claim, and the row is left <c>Sending</c> with no verdict.
    /// This method writes that verdict, in the same sweep, so the command does not sit claimed
    /// until its claim expires.
    /// </para>
    ///
    /// <para>
    /// <b>It does not cover a killed worker</b>, and it cannot: a process that is OOM-killed or
    /// evicted runs no catch block, so nothing here executes at all. That case is closed one
    /// level up, by <c>IntegrationCommand.IsDueAt</c> treating a <c>Sending</c> command whose
    /// claim has expired as due again. The two are complementary — this one is fast and precise,
    /// that one is slow and unconditional — and only together do they leave no command stranded.
    /// </para>
    ///
    /// <para>
    /// It applies the same budget the handler would have: reschedule while
    /// <see cref="ICommandRetryPolicy.MaxAttempts"/> allows, reject once it does not — through the
    /// same <see cref="ICommandRetryPolicy"/> instance, so there is still exactly one curve and
    /// one ceiling in the platform. The rejection carries the <c>Transient</c> family even though
    /// the cause was our own crash: the family tells an administrator whether retrying could ever
    /// help, and a fault on our side is precisely a thing that can pass.
    /// </para>
    ///
    /// <para>
    /// A command left <c>Pending</c> instead (the attempt threw before the claim — a validation
    /// failure) is claimed here first, on purpose. Left Pending it would be re-sent every single
    /// minute for as long as the fault lasts: a hot loop against a dead CBS and a call log nobody
    /// can read. Through the budget it backs off like any other failure and ends up where a human
    /// will see it.
    /// </para>
    /// </summary>
    private static async Task RecoverFaultedAttemptAsync(
        IntegrationDbContext db,
        ICommandRetryPolicy retryPolicy,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid commandId,
        Exception cause,
        DispatchSweepReport report,
        CancellationToken ct)
    {
        // Read back the COMMITTED state: the handler's transaction has either landed or rolled
        // back, and only the row says which.
        var command = await db.Commands
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == commandId && c.TenantId == tenantId, ct);

        if (command is null)
        {
            logger.LogWarning(
                "Integration command {CommandId} vanished while its attempt was faulting.",
                commandId);
            return;
        }

        // Succeeded, Rejected or Cancelled: a verdict was committed before the throw and it
        // stands. Batched: waiting for an acknowledgement, which is INT-25's business, not a
        // retry. RetryScheduled: the handler already scheduled it.
        if (!command.IsOpen
            || command.Status is CommandStatus.Batched or CommandStatus.RetryScheduled)
            return;

        if (command.Status == CommandStatus.Pending) command.BeginSending(clock);

        if (command.Status != CommandStatus.Sending) return;

        // The exception's own message, truncated by IntegrationCommand.Describe. Its type and
        // stack trace are already in the logged error above; what the stored row needs is
        // something an operator can act on, and it must never become a payload dump.
        var detail = cause.Message;

        if (command.Attempts >= retryPolicy.MaxAttempts)
        {
            command.Reject(ErrorFamily.Transient, IntegrationErrors.Unavailable, detail, clock);
            report.Rejected++;

            logger.LogWarning(
                "Integration command {CommandId} rejected after {Attempts} faulted attempt(s).",
                commandId, command.Attempts);
        }
        else
        {
            command.ScheduleRetry(
                retryPolicy.NextAttemptAt(command.Attempts),
                IntegrationErrors.Unavailable, detail, clock);

            report.Rescheduled++;
        }

        report.Recovered++;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A due command, projected to what the dispatcher needs: an identifier and the three keys
    /// that decide its order. Deliberately no payload and no status.
    /// </summary>
    private sealed record DueCommand(Guid Id, string EntityType, Guid CrmId, DateTimeOffset CreatedAt);
}

/// <summary>What one sweep did, for the log line and for the tests.</summary>
internal sealed class DispatchSweepReport
{
    public int Attempted { get; set; }

    /// <summary>Commands that left the platform — called successfully, or enlisted in a file.</summary>
    public int Succeeded { get; set; }

    public int Rescheduled { get; set; }
    public int Rejected { get; set; }

    /// <summary>Entity queues that stopped at a non-success and will resume next sweep.</summary>
    public int GroupsHalted { get; set; }

    /// <summary>Attempts whose handler threw rather than recording an outcome.</summary>
    public int Faulted { get; set; }

    /// <summary>Faulted attempts whose command was taken back out of <c>Sending</c>.</summary>
    public int Recovered { get; set; }
}
