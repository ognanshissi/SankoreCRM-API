namespace Sankore.Integration.RelayAgent.Observability;

using Microsoft.Extensions.Logging;
using Sankore.Integration.RelayAgent.Protocol;

/// <summary>
/// The ONLY way this agent logs anything about an order, and the reviewable form of criterion 4
/// ("its logs contain no personal data").
///
/// <para>
/// A promise in a comment is not checkable. A promise enforced by a signature is: every method
/// here takes only values that cannot carry relayed content —
/// </para>
/// <list type="bullet">
///   <item>the correlation id, which SANKORE generated and which identifies an order, not a person;</item>
///   <item>the order kind, an enum of four values;</item>
///   <item>the target name, a label from the agent's own configuration file;</item>
///   <item>the outcome, an enum of three values;</item>
///   <item>an error code from <see cref="RelayErrorCodes"/>, a closed set of constants;</item>
///   <item>a duration, a byte count, a row count — numbers.</item>
/// </list>
/// <para>
/// There is no overload taking a payload, a file's bytes, a SQL row, an HTTP body, a URL, a host
/// name, a path, or an <see cref="Exception"/>. The last one matters most: an exception's message
/// is where the personal data actually leaks — SSH.NET names the remote path, Npgsql names the
/// column and sometimes the value, HttpClient names the host. <see cref="OrderFailed"/> therefore
/// takes an exception TYPE name, which the caller obtains with
/// <c>ex.GetType().Name</c>, and never the exception.
/// </para>
///
/// <para>
/// <b>How to review this</b>: grep the project for <c>_logger.Log</c>, <c>LogInformation</c>,
/// <c>LogWarning</c>, <c>LogError</c> and <c>LogDebug</c> inside <c>Execution/</c>. There should
/// be none — the executors receive no <see cref="ILogger"/> at all, which is the structural half
/// of the guarantee. Everything about an order is logged here, from the dispatcher.
/// <c>RelayLogRedactionTests</c> in the test project asserts it against real payloads.
/// </para>
///
/// <para>
/// Static and not a <c>LoggerMessage</c> source-generated class: the generator would produce one
/// partial method per shape and the shapes are the point of this file, so they are written out
/// where a reviewer reads them.
/// </para>
/// </summary>
public static class RelayLog
{
    /// <summary>Logged when an order arrives, before anything is attempted.</summary>
    public static void OrderReceived(
        ILogger logger, string correlationId, RelayOrderKind kind, string target)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogInformation(
            "Relay order received. correlationId={CorrelationId} kind={Kind} target={Target}",
            correlationId, kind, target);
    }

    /// <summary>
    /// Logged on success. <paramref name="resultBytes"/> is the SIZE of what is being handed
    /// back, never its content — a size is what tells an operator "the file was empty" without
    /// telling them whose file it was.
    /// </summary>
    public static void OrderSucceeded(
        ILogger logger,
        string correlationId,
        RelayOrderKind kind,
        string target,
        long durationMs,
        int resultBytes)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogInformation(
            "Relay order succeeded. correlationId={CorrelationId} kind={Kind} target={Target} "
            + "durationMs={DurationMs} resultBytes={ResultBytes}",
            correlationId, kind, target, durationMs, resultBytes);
    }

    /// <summary>
    /// Logged on refusal or unavailability. <paramref name="exceptionTypeName"/> is a type name
    /// such as <c>SocketException</c> — never a message, see the type comment.
    /// </summary>
    public static void OrderFailed(
        ILogger logger,
        string correlationId,
        RelayOrderKind kind,
        string target,
        RelayOutcome outcome,
        string errorCode,
        long durationMs,
        string? exceptionTypeName)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogWarning(
            "Relay order failed. correlationId={CorrelationId} kind={Kind} target={Target} "
            + "outcome={Outcome} errorCode={ErrorCode} durationMs={DurationMs} "
            + "exceptionType={ExceptionType}",
            correlationId, kind, target, outcome, errorCode, durationMs,
            exceptionTypeName ?? "-");
    }

    /// <summary>Session lifecycle. The URI logged is SANKORE's, from our own configuration.</summary>
    public static void SessionOpened(ILogger logger, Uri channelUri, string agentVersion)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogInformation(
            "Relay session opened to SANKORE. uri={ChannelUri} agentVersion={AgentVersion}",
            channelUri, agentVersion);
    }

    /// <summary>
    /// Session lost. Same discipline as an order: the exception type, not its message — a TLS
    /// failure's message can quote the certificate subject, which identifies the IMF.
    /// </summary>
    public static void SessionLost(
        ILogger logger, string? exceptionTypeName, TimeSpan retryIn, int attempt)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogWarning(
            "Relay session lost. exceptionType={ExceptionType} attempt={Attempt} "
            + "retryInMs={RetryInMs}",
            exceptionTypeName ?? "-", attempt, (long)retryIn.TotalMilliseconds);
    }

    /// <summary>Heartbeat sent. Counts only — the frame's own contents are already non-personal.</summary>
    public static void HeartbeatSent(
        ILogger logger, RelayAgentState state, int targetCount, int ordersInFlight)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogDebug(
            "Relay heartbeat sent. state={State} targets={TargetCount} inFlight={OrdersInFlight}",
            state, targetCount, ordersInFlight);
    }

    /// <summary>A frame whose type tag this agent does not implement. Declined, session kept.</summary>
    public static void FrameDeclined(ILogger logger, string frameType)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogWarning("Relay frame declined, unknown type. type={FrameType}", frameType);
    }
}
