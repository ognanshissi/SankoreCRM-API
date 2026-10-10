namespace Sankore.Integration.RelayAgent.Execution;

using System.Text.Json;
using Sankore.Integration.RelayAgent.Observability;
using Sankore.Integration.RelayAgent.Protocol;

/// <summary>
/// What an executor hands back: an outcome, a code from the closed set, and the relayed answer.
///
/// <para>
/// A result and never an exception, for the same reason M02's <c>BiometryResult&lt;T&gt;</c> is:
/// an unreachable system is a normal, expected, retryable condition, and letting it surface as an
/// exception would make a managed outage indistinguishable from a bug — and would put the
/// library's message, which names hosts and paths, into a stack trace somebody logs.
/// </para>
///
/// <para>
/// <paramref name="ReachedTarget"/> is separate from the outcome because the heartbeat needs a
/// different question answered. A view that answered "column out of range" was REACHED; a refused
/// order never left this process and says nothing about the target at all. Reporting the first as
/// unreachable would have an operator restarting a healthy database.
/// </para>
/// </summary>
public sealed record RelayExecution(
    RelayOutcome Outcome,
    string? ErrorCode,
    JsonElement? Payload,
    string? ExceptionTypeName,
    bool ReachedTarget)
{
    /// <summary>The relayed system answered.</summary>
    public static RelayExecution Ok(JsonElement payload)
        => new(RelayOutcome.Succeeded, null, payload, null, ReachedTarget: true);

    /// <summary>
    /// The agent declined. <paramref name="reachedTarget"/> defaults to false: a refusal is
    /// decided here, before anything is dialled, so it carries no information about the target.
    /// </summary>
    public static RelayExecution Refuse(string errorCode)
        => new(RelayOutcome.Refused, errorCode, null, null, ReachedTarget: false);

    /// <summary>The relayed system did not answer, or answered an error.</summary>
    public static RelayExecution Unavailable(
        string errorCode, string? exceptionTypeName = null, bool reachedTarget = false)
        => new(RelayOutcome.Unavailable, errorCode, null, exceptionTypeName, reachedTarget);

    /// <summary>
    /// The catch-all. Separate factory so a reviewer can grep for it: every use is a place where
    /// detail is being dropped on purpose (see <see cref="RelayErrorCodes"/>).
    /// </summary>
    public static RelayExecution Unexpected(Exception exception)
        => new(
            RelayOutcome.Unavailable,
            RelayErrorCodes.RelayUnexpectedError,
            null,
            exception?.GetType().Name,
            ReachedTarget: false);
}
