namespace Sankore.Integration.RelayAgent.Protocol;

using System.Text.Json;
using System.Text.Json.Serialization;

// ---------------------------------------------------------------------------------------------
// The relay wire contract (INT-26).
//
// *** THE SANKORE SIDE OF THIS CONTRACT DOES NOT EXIST YET. ***
//
// There is no channel host in the platform: nothing in Sankore.Api accepts a WebSocket from an
// agent, nothing issues these orders, nothing consumes these results or heartbeats. This folder
// is one half of a contract, and the two halves must be written together — the day the host is
// built, these records are what it has to mirror, field for field, or every frame deserialises
// to nulls and the agent answers RELAY_FRAME_INVALID to orders a working platform sent.
//
// Deliberately NOT shared through a PublicApi project: this process must stay buildable and
// deployable with no reference to the Integration module (see the csproj's closing comment). The
// cost is that the two definitions can drift; PROTOCOL_VERSION below is how a drift announces
// itself at the handshake instead of as a mapping that silently yields null — which is exactly
// the failure M02's biometry client lived through.
//
// Plain records over System.Text.Json, no polymorphic serialisation: a frame is a type tag plus
// a raw JSON body, decoded by the one handler that claims the tag. A JsonPolymorphic hierarchy
// would make an unknown tag a deserialisation exception instead of a frame we can decline and
// keep the session alive for.
// ---------------------------------------------------------------------------------------------

/// <summary>Frame type tags. Strings and not an enum: an unknown tag must be declinable.</summary>
public static class RelayFrameTypes
{
    /// <summary>Agent → SANKORE, first frame of every session.</summary>
    public const string Hello = "hello";

    /// <summary>SANKORE → agent, the answer to <see cref="Hello"/>.</summary>
    public const string Welcome = "welcome";

    /// <summary>SANKORE → agent, one unit of work.</summary>
    public const string Order = "order";

    /// <summary>Agent → SANKORE, the outcome of one order.</summary>
    public const string Result = "result";

    /// <summary>Agent → SANKORE, periodic liveness and latency report.</summary>
    public const string Heartbeat = "heartbeat";
}

/// <summary>
/// Every frame in both directions. <paramref name="Body"/> is left as raw JSON so the receiving
/// side decodes it only once it has recognised <paramref name="Type"/>.
/// </summary>
public sealed record RelayFrame(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("body")] JsonElement Body);

/// <summary>
/// The agent announcing itself. Carries no credential: the client certificate presented during
/// the TLS handshake already identified this agent, and a second identity on the wire would be a
/// second thing to steal.
/// </summary>
public sealed record RelayHello(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("declaredTargets")] IReadOnlyList<RelayDeclaredTarget> DeclaredTargets);

/// <summary>
/// One target the agent is willing to act on, as declared in its configuration file.
///
/// <para>
/// Sent at the handshake so the platform can tell an operator which relayed systems this agent
/// actually serves, and so an order naming an undeclared target can be refused by the platform
/// before it is even sent. The agent refuses it again on arrival — the platform's check is a
/// convenience, the agent's is the guarantee.
/// </para>
/// </summary>
public sealed record RelayDeclaredTarget(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] RelayOrderKind Kind);

/// <summary>SANKORE accepting the session. <paramref name="SessionId"/> is for correlation only.</summary>
public sealed record RelayWelcome(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("heartbeatSeconds")] int? HeartbeatSeconds);

/// <summary>What an order asks the agent to do, inside the IMF's network.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RelayOrderKind>))]
public enum RelayOrderKind
{
    /// <summary>Call a declared local HTTP base URL.</summary>
    HttpCall,

    /// <summary>Deposit one file in a declared SFTP directory.</summary>
    SftpPut,

    /// <summary>Read one file back from a declared SFTP directory.</summary>
    SftpRead,

    /// <summary>Select from a declared, read-only SQL view.</summary>
    SqlView
}

/// <summary>
/// One unit of work.
///
/// <para>
/// <b><paramref name="Target"/> is a NAME, never an address.</b> It is looked up in the agent's
/// own configuration file, which is the only place a URL, a host, a directory or a view name
/// exists. The wire says *which declared target and what payload*; the file says *where it is*.
/// There is no frame in this contract that carries a URL, a hostname or a SQL statement, and
/// that absence is the component's security argument — a relay that executed an address or a
/// query it was sent would be a remote-code-execution channel into a bank's network.
/// </para>
/// </summary>
public sealed record RelayOrder(
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("kind")] RelayOrderKind Kind,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("body")] JsonElement Body,
    [property: JsonPropertyName("timeoutSeconds")] int? TimeoutSeconds);

/// <summary>
/// Body of a <see cref="RelayOrderKind.HttpCall"/>.
///
/// <para>
/// <paramref name="Path"/> is appended to the target's configured base URL and must stay under
/// it; it is not a URL. No header comes from the wire: an order that could set headers could set
/// <c>Authorization</c> or <c>Host</c>, i.e. authenticate as somebody else or reach a different
/// virtual host on the same address. Headers are per-target, in the file.
/// </para>
/// </summary>
public sealed record RelayHttpCallBody(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("query")] IReadOnlyDictionary<string, string>? Query,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("body")] string? Body);

/// <summary>
/// Body of a <see cref="RelayOrderKind.SftpPut"/>. <paramref name="FileName"/> is a bare file
/// name with no directory component — the directory is the target's, from the file.
/// </summary>
public sealed record RelaySftpPutBody(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("contentBase64")] string ContentBase64);

/// <summary>Body of a <see cref="RelayOrderKind.SftpRead"/>.</summary>
public sealed record RelaySftpReadBody(
    [property: JsonPropertyName("fileName")] string FileName);

/// <summary>
/// Body of a <see cref="RelayOrderKind.SqlView"/>: filter values only.
///
/// <para>
/// There is no field here for a query, a table, a column list or an ordering, and there never
/// will be one. The view, the columns that may be filtered on and the row cap are declared in
/// the configuration file; this record supplies values that are bound as parameters.
/// </para>
/// </summary>
public sealed record RelaySqlViewBody(
    [property: JsonPropertyName("parameters")] IReadOnlyDictionary<string, string?> Parameters);

/// <summary>
/// The outcome of one order, sent back on the same session.
///
/// <para>
/// <paramref name="Payload"/> is the only place relayed data travels: an HTTP response body, a
/// file's bytes, a view's rows. It is held in memory for the length of one order and then
/// dropped — never written to disk, never logged (see <c>Observability/RelayLog.cs</c>).
/// </para>
/// </summary>
public sealed record RelayOrderResult(
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("kind")] RelayOrderKind Kind,
    [property: JsonPropertyName("outcome")] RelayOutcome Outcome,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("payload")] JsonElement? Payload);

/// <summary>
/// How an order ended. Three values and not a boolean, because the platform's retry decision
/// differs: <see cref="Unavailable"/> is worth retrying, <see cref="Refused"/> never is.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RelayOutcome>))]
public enum RelayOutcome
{
    /// <summary>The relayed system answered.</summary>
    Succeeded,

    /// <summary>The agent declined the order itself: undeclared target, undeclared parameter,
    /// a file name with a path in it. Resending it unchanged will be declined again.</summary>
    Refused,

    /// <summary>The relayed system did not answer, or answered an error. Retryable.</summary>
    Unavailable
}

/// <summary>
/// The heartbeat (criterion 5): the agent's version, its state, and the latency it measures
/// towards each system it relays.
///
/// <para>
/// Everything here is drawn from the configuration file and from clocks. A target's NAME is an
/// operator's label ("cbs-core"), never an address and never a person — which is what makes this
/// frame safe to store on the platform and to show in a dashboard.
/// </para>
/// </summary>
public sealed record RelayHeartbeat(
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("state")] RelayAgentState State,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("ordersInFlight")] int OrdersInFlight,
    [property: JsonPropertyName("targets")] IReadOnlyList<RelayTargetHealth> Targets);

/// <summary>
/// The agent's own state, as the agent sees it. <see cref="Degraded"/> is the interesting one:
/// the session to SANKORE is fine and a relayed system is not, which is the failure an operator
/// is most likely to misread as "the integration is down".
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RelayAgentState>))]
public enum RelayAgentState
{
    /// <summary>Session up, every declared target reachable.</summary>
    Healthy,

    /// <summary>Session up, at least one declared target unreachable.</summary>
    Degraded
}

/// <summary>Latency and reachability of one declared target.</summary>
public sealed record RelayTargetHealth(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] RelayOrderKind Kind,
    [property: JsonPropertyName("state")] RelayTargetState State,
    [property: JsonPropertyName("latencyMs")] long? LatencyMs,
    [property: JsonPropertyName("checkedAt")] DateTimeOffset? CheckedAt);

/// <summary>Reachability of one declared target.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RelayTargetState>))]
public enum RelayTargetState
{
    /// <summary>Never probed and never used since this process started.</summary>
    Unknown,

    /// <summary>Last probe or order reached it.</summary>
    Reachable,

    /// <summary>Last probe or order did not reach it.</summary>
    Unreachable
}

/// <summary>Serialisation settings, identical in both directions.</summary>
public static class RelayProtocolJson
{
    /// <summary>
    /// Bumped whenever a frame changes shape. The handshake compares it so a mismatched pair
    /// fails loudly at connection time rather than quietly on the first order.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// camelCase is already explicit on every property above; the naming policy is set anyway so
    /// a property added without its attribute still matches the platform's default.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
