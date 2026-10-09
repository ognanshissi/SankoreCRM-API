namespace Sankore.Modules.Integration.Infrastructure.CallLog;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The one writer of <c>integration_call_log</c> (INT-08).
///
/// <para>
/// Three guarantees, each of which exists because its absence was the cheaper thing to write:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>A row on every path.</b> Success, every failure family, and the operation throwing. A
/// journal that only records successes is worse than none: the gaps look like idle periods, so
/// the question "was the CBS down at 14:05" gets a confident wrong answer instead of no answer.
/// That is why the throw branch records a <c>Technical</c> row and <i>then</i> rethrows — the
/// caller's error handling is unchanged, and the call still leaves a trace.
/// </item>
/// <item>
/// <b>The write never takes the caller down.</b> A failed append is logged and swallowed. A
/// compliance journal that rolled back the business call it describes would convert an audit
/// problem into an outage — the customer's account would not be opened because we could not
/// write down that we opened it. The one exception is
/// <see cref="OperationCanceledException"/>, which means the process is being torn down and is
/// nobody's business to absorb.
/// </item>
/// <item>
/// <b>It commits on its own.</b> See <see cref="ICallLogStore"/>: the append runs outside the
/// ambient transaction of the command that triggered the call, because a <c>Rejected</c> result
/// rolls that transaction back and the evidence of the rejected call must survive it.
/// </item>
/// </list>
///
/// <para>
/// Nothing here reads <c>IntegrationResult.Detail</c> into a column or a log template; see
/// <see cref="CallLogRedaction"/> for why that omission is the load-bearing half of criterion 2.
/// </para>
/// </summary>
internal sealed class CallJournal(
    ICallLogStore store,
    TimeProvider clock,
    ILogger<CallJournal> logger) : ICallJournal
{
    /// <summary>
    /// The header an adapter must put <see cref="CallContext.Correlation"/> on. Same name as M02
    /// uses towards the biometric service, so one convention spans every outbound integration of
    /// the platform and a support request does not have to ask which header to grep.
    /// </summary>
    internal const string CorrelationHeader = "X-Correlation-Id";

    public async Task<IntegrationResult<T>> RecordAsync<T>(
        CallContext context,
        Func<CancellationToken, Task<IntegrationResult<T>>> operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        // Fails here rather than inside the append, where the DomainException would be swallowed
        // with every other write failure and the adapter author would see only a missing row.
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Operation);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await operation(ct);
            stopwatch.Stop();

            await AppendAsync(context, stopwatch.ElapsedMilliseconds, result.Family, result.Code, result.Detail);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await AppendFaultAsync(context, stopwatch.ElapsedMilliseconds, ex);
            throw;
        }
    }

    public async Task<IntegrationResult> RecordAsync(
        CallContext context,
        Func<CancellationToken, Task<IntegrationResult>> operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        // Fails here rather than inside the append, where the DomainException would be swallowed
        // with every other write failure and the adapter author would see only a missing row.
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Operation);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await operation(ct);
            stopwatch.Stop();

            await AppendAsync(context, stopwatch.ElapsedMilliseconds, result.Family, result.Code, result.Detail);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await AppendFaultAsync(context, stopwatch.ElapsedMilliseconds, ex);
            throw;
        }
    }

    /// <summary>
    /// The exception path. Classified rather than lumped together: an adapter whose HTTP stack
    /// threw has a configuration or a code problem (<c>Technical</c> — retrying cannot fix it and
    /// an administrator should hear about it), while the caller cancelling is an operational fact
    /// about us and not a refusal by the back-office (<c>Transient</c>).
    ///
    /// <para>
    /// The exception's message is NOT recorded. A <c>JsonException</c> or a
    /// <c>DbUpdateException</c> quotes the offending value, which on this path is the payload —
    /// the single thing the journal exists not to contain. The type name goes to the application
    /// log, the stack trace stays with the rethrown exception, and the row carries a stable code.
    /// </para>
    /// </summary>
    private async Task AppendFaultAsync(CallContext context, long durationMs, Exception ex)
    {
        var cancelled = ex is OperationCanceledException;

        logger.LogError(
            ex,
            "Integration call faulted | Operation={Operation} Connection={ConnectionId} "
            + "Correlation={Correlation} Duration={DurationMs}ms Exception={ExceptionType}",
            context.Operation, context.ConnectionId, context.Correlation, durationMs, ex.GetType().Name);

        await AppendAsync(
            context,
            durationMs,
            cancelled ? ErrorFamily.Transient : ErrorFamily.Technical,
            cancelled ? CallLogRedaction.CancelledCode : CallLogRedaction.UnhandledExceptionCode,
            detail: null);
    }

    /// <summary>
    /// Builds and appends the row.
    ///
    /// <para>
    /// The store is called with <see cref="CancellationToken.None"/> and not with the caller's
    /// token. The row describes a call that has <i>already happened</i>; cancelling its write
    /// destroys evidence of an exchange with a back-office that cannot be replayed, and the
    /// cancellation path — a shutdown, an abandoned request — is precisely when a journal is
    /// being asked what went on. The write is a single bounded INSERT, so it cannot be what makes
    /// a shutdown hang.
    /// </para>
    /// </summary>
    private async Task AppendAsync(
        CallContext context, long durationMs, ErrorFamily? family, string? code, string? detail)
    {
        try
        {
            var row = IntegrationCallLog.Record(
                tenantId: context.TenantId,
                connectionId: context.ConnectionId,
                operation: CallLogRedaction.BoundOperation(context.Operation),
                durationMs: durationMs,
                at: clock.GetUtcNow(),
                commandId: context.CommandId,
                // Passed as given: the entity's own Sanitize drops the query string and the
                // fragment and caps the length, and that belongs to the entity so that no writer
                // — here, a backfill, a future adapter — can bypass it.
                endpoint: context.Endpoint,
                httpStatus: context.Probe.HttpStatus,
                errorFamily: family,
                errorCode: CallLogRedaction.BoundErrorCode(code),
                correlationId: context.Correlation);

            await store.AppendAsync(row, CancellationToken.None);

            // Debug, not Information: one line per call per tenant at Information would drown Seq
            // in the exact traffic this table already records durably. The detail is described,
            // never quoted — criterion 2 covers the application logs too.
            logger.LogDebug(
                "Integration call journalled | Operation={Operation} Connection={ConnectionId} "
                + "Status={HttpStatus} Family={ErrorFamily} Code={ErrorCode} Duration={DurationMs}ms "
                + "Correlation={Correlation} Detail={DetailDescription}",
                row.Operation, context.ConnectionId, context.Probe.HttpStatus, family, row.ErrorCode,
                durationMs, context.Correlation, CallLogRedaction.DescribeDetail(detail));
        }
        catch (OperationCanceledException)
        {
            // Not ours to absorb: the host is going down, and the store was given None, so this
            // can only come from the store's own plumbing.
            throw;
        }
        catch (Exception ex)
        {
            // Swallowed on purpose — see the class summary. Logged as an error because a journal
            // that has started losing rows is an incident of its own, even though it is not the
            // business call's incident.
            logger.LogError(
                ex,
                "Could not journal an integration call | Operation={Operation} "
                + "Connection={ConnectionId} Correlation={Correlation}",
                context.Operation, context.ConnectionId, context.Correlation);
        }
    }
}
