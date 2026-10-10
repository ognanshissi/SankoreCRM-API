namespace Sankore.Integration.RelayAgent.Execution;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sankore.Integration.RelayAgent.Observability;
using Sankore.Integration.RelayAgent.Protocol;

/// <summary>
/// Routes an order to the executor that claims its kind, times it, feeds the heartbeat, and is
/// the ONE place in this project that logs anything about an order.
///
/// <para>
/// That centralisation is criterion 4's enforcement point. The executors hold no
/// <see cref="ILogger"/> at all, so "no personal data in the logs" is not a rule they have to
/// keep; it is a capability they do not have. Here, every log line is emitted through
/// <see cref="RelayLog"/>, whose signatures accept only a correlation id, an enum, a target name
/// from the configuration file, a code from a closed set and some numbers.
/// </para>
///
/// <para>
/// Nothing is retained. An order is executed, its answer is handed to the caller to put on the
/// session, and the only trace left behind is a counter in
/// <see cref="TargetHealthRegistry"/> — a state and a latency, per declared target name.
/// </para>
/// </summary>
public sealed class RelayOrderDispatcher
{
    private readonly Dictionary<RelayOrderKind, IRelayOrderExecutor> _executors;
    private readonly TargetHealthRegistry _health;
    private readonly ILogger<RelayOrderDispatcher> _logger;

    public RelayOrderDispatcher(
        IEnumerable<IRelayOrderExecutor> executors,
        TargetHealthRegistry health,
        ILogger<RelayOrderDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _executors = executors.ToDictionary(e => e.Kind);
        _health = health;
        _logger = logger;
    }

    /// <summary>
    /// Executes one order and builds the frame that answers it. Never throws: a session carrying
    /// several orders must not lose the others because one of them hit something unforeseen.
    /// </summary>
    public async Task<RelayOrderResult> DispatchAsync(
        RelayOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        RelayLog.OrderReceived(_logger, order.CorrelationId, order.Kind, order.Target);

        var stopwatch = Stopwatch.StartNew();

        if (!_executors.TryGetValue(order.Kind, out var executor))
        {
            // A kind this build does not implement. Refused rather than treated as unavailable:
            // the platform must not retry it, because no amount of retrying adds an executor.
            return Fail(
                order, RelayOutcome.Refused, RelayErrorCodes.FrameInvalid,
                stopwatch.ElapsedMilliseconds, null, observeTarget: false);
        }

        RelayExecution execution;
        try
        {
            execution = await executor.ExecuteAsync(order, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down. Reported so the platform can requeue, and NOT recorded against the
            // target — our stopping says nothing about their system.
            return Fail(
                order, RelayOutcome.Unavailable, RelayErrorCodes.TargetUnreachable,
                stopwatch.ElapsedMilliseconds, nameof(OperationCanceledException),
                observeTarget: false);
        }
        catch (Exception ex)
        {
            // An executor is contracted not to throw (see IRelayOrderExecutor). This catch is the
            // admission that a contract is not a guarantee: a bug here must cost one order, not
            // the session. The exception's TYPE is kept, its message is not — that message is
            // where a remote path or a SQL row would travel.
            execution = RelayExecution.Unexpected(ex);
        }

        var elapsed = stopwatch.ElapsedMilliseconds;

        if (execution.Outcome != RelayOutcome.Refused)
            _health.RecordOrder(order.Target, execution.ReachedTarget, elapsed);

        if (execution.Outcome == RelayOutcome.Succeeded)
        {
            RelayLog.OrderSucceeded(
                _logger, order.CorrelationId, order.Kind, order.Target, elapsed,
                PayloadSize(execution));

            return new RelayOrderResult(
                order.CorrelationId, order.Kind, RelayOutcome.Succeeded, null, elapsed,
                execution.Payload);
        }

        RelayLog.OrderFailed(
            _logger, order.CorrelationId, order.Kind, order.Target, execution.Outcome,
            execution.ErrorCode ?? RelayErrorCodes.RelayUnexpectedError, elapsed,
            execution.ExceptionTypeName);

        return new RelayOrderResult(
            order.CorrelationId, order.Kind, execution.Outcome,
            execution.ErrorCode ?? RelayErrorCodes.RelayUnexpectedError, elapsed, null);
    }

    /// <summary>
    /// Builds a refusal this dispatcher decided itself, keeping the log call on the same path as
    /// every other failure.
    /// </summary>
    private RelayOrderResult Fail(
        RelayOrder order,
        RelayOutcome outcome,
        string errorCode,
        long elapsed,
        string? exceptionTypeName,
        bool observeTarget)
    {
        if (observeTarget)
            _health.RecordOrder(order.Target, reached: false, elapsed);

        RelayLog.OrderFailed(
            _logger, order.CorrelationId, order.Kind, order.Target, outcome, errorCode, elapsed,
            exceptionTypeName);

        return new RelayOrderResult(
            order.CorrelationId, order.Kind, outcome, errorCode, elapsed, null);
    }

    /// <summary>
    /// The SIZE of what is being handed back, for the log line. A size is the most an operator
    /// can be told about a payload — enough to see "the file was empty", nothing about whose.
    /// </summary>
    private static int PayloadSize(RelayExecution execution)
        => execution.Payload is { } payload
            ? payload.GetRawText().Length
            : 0;
}
