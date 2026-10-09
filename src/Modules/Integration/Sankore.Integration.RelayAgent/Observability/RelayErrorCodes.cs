namespace Sankore.Integration.RelayAgent.Observability;

/// <summary>
/// The CLOSED set of error codes this agent will ever put on the wire or in a log line.
///
/// <para>
/// Closed on purpose, and this is criterion 4 in its most load-bearing form. The natural way to
/// report a failure is to pass the exception's message through — and an SFTP library's message
/// names the remote path, Npgsql's names the column and sometimes the offending value, and
/// HttpClient's names the host. Those are the three things that must not leave this process in a
/// log. So nothing here is derived from an exception: every code is a constant chosen at the call
/// site, and the exception itself is reduced to its TYPE name (see <c>RelayLog</c>).
/// </para>
///
/// <para>
/// A failure with no matching code is reported as <see cref="RelayUnexpectedError"/> rather than
/// as its own message. That loses detail, which is the trade accepted: an operator diagnoses from
/// the code plus the correlation id plus the target name, and the detail lives on the relayed
/// system's own logs, inside the IMF, where it is allowed to.
/// </para>
/// </summary>
public static class RelayErrorCodes
{
    // --- Refused by the agent itself. Resending the same order changes nothing. ---

    /// <summary>The order named a target that is not in the configuration file's allow-list.</summary>
    public const string TargetNotDeclared = "RELAY_TARGET_NOT_DECLARED";

    /// <summary>The order's kind does not match the kind the named target was declared as.</summary>
    public const string TargetKindMismatch = "RELAY_TARGET_KIND_MISMATCH";

    /// <summary>The frame could not be decoded into the body its kind requires.</summary>
    public const string FrameInvalid = "RELAY_FRAME_INVALID";

    /// <summary>An HTTP method the target was not declared to allow.</summary>
    public const string MethodNotAllowed = "RELAY_METHOD_NOT_ALLOWED";

    /// <summary>A path that leaves the target's configured base URL, or is not relative.</summary>
    public const string PathOutsideBase = "RELAY_PATH_OUTSIDE_BASE";

    /// <summary>A file name carrying a directory component, or characters outside the allow-list.</summary>
    public const string FileNameInvalid = "RELAY_FILE_NAME_INVALID";

    /// <summary>A filter column the view was not declared to accept.</summary>
    public const string ParameterNotDeclared = "RELAY_PARAMETER_NOT_DECLARED";

    /// <summary>A direction (deposit or read) the SFTP target was not declared to allow.</summary>
    public const string DirectionNotAllowed = "RELAY_DIRECTION_NOT_ALLOWED";

    /// <summary>The order's payload, or the answer to it, exceeds the in-memory cap.</summary>
    public const string PayloadTooLarge = "RELAY_PAYLOAD_TOO_LARGE";

    /// <summary>The agent is already running as many orders as it is configured to.</summary>
    public const string AgentBusy = "RELAY_AGENT_BUSY";

    // --- Unavailable. The relayed system, not the order. Retryable by the platform. ---

    /// <summary>The relayed system did not answer within the order's budget.</summary>
    public const string TargetTimeout = "RELAY_TARGET_TIMEOUT";

    /// <summary>The relayed system could not be reached, or refused the connection.</summary>
    public const string TargetUnreachable = "RELAY_TARGET_UNREACHABLE";

    /// <summary>The relayed system answered an error status.</summary>
    public const string TargetError = "RELAY_TARGET_ERROR";

    /// <summary>
    /// The SFTP host key did not match the fingerprint in the configuration file. Deliberately
    /// NOT folded into <see cref="TargetUnreachable"/>: an unreachable host is an outage, a
    /// changed host key is either a rotated server or a machine-in-the-middle, and the two want
    /// opposite reactions from whoever reads it.
    /// </summary>
    public const string HostKeyRefused = "RELAY_HOST_KEY_REFUSED";

    /// <summary>Anything else. Carries no detail by design — see the type comment.</summary>
    public const string RelayUnexpectedError = "RELAY_UNEXPECTED_ERROR";
}
