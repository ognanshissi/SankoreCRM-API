namespace Sankore.Integration.RelayAgent.Configuration;

using Sankore.Integration.RelayAgent.Protocol;

/// <summary>
/// The configuration file (criterion 1: "configured by a file and a client certificate").
///
/// <para>
/// This file is the agent's whole authority. Every address the agent is capable of reaching —
/// a local HTTP base URL, an SFTP host and directory, a database and a view — exists HERE and
/// nowhere else. The wire never supplies one; it names a target declared below. So the set of
/// things this agent can do inside the IMF's network is fixed by the IMF's own IT team, in a
/// file on their own machine, and cannot be widened by anything SANKORE sends.
/// </para>
///
/// <para>
/// Bound from the section <c>Relay</c>, validated at start-up and on every value
/// (<see cref="RelayAgentOptionsValidator"/>): a nonsensical entry must be a boot failure naming
/// the key, not a refusal on the first order hours later, in a log the IT team is not watching.
/// </para>
/// </summary>
public sealed class RelayAgentOptions
{
    /// <summary>Bound from <c>Relay</c>.</summary>
    public const string SectionName = "Relay";

    /// <summary>Where and how to dial SANKORE.</summary>
    public RelaySankoreOptions Sankore { get; set; } = new();

    /// <summary>The client certificate that identifies this agent during the TLS handshake.</summary>
    public RelayCertificateOptions Certificate { get; set; } = new();

    /// <summary>Reconnection policy.</summary>
    public RelayReconnectOptions Reconnect { get; set; } = new();

    /// <summary>Heartbeat cadence and probe budget.</summary>
    public RelayHeartbeatOptions Heartbeat { get; set; } = new();

    /// <summary>Declared local HTTP targets. An order may reach these and no other address.</summary>
    public IList<RelayHttpTargetOptions> HttpTargets { get; } = [];

    /// <summary>Declared SFTP targets.</summary>
    public IList<RelaySftpTargetOptions> SftpTargets { get; } = [];

    /// <summary>Declared read-only SQL views.</summary>
    public IList<RelaySqlViewTargetOptions> SqlViewTargets { get; } = [];

    /// <summary>
    /// Every declared target, flattened, for the handshake's <see cref="RelayDeclaredTarget"/>
    /// list and for the heartbeat. One flat name space across the three kinds, which is why the
    /// validator refuses a name used twice.
    /// </summary>
    public IEnumerable<(string Name, RelayOrderKind Kind)> AllTargets()
    {
        foreach (var t in HttpTargets) yield return (t.Name, RelayOrderKind.HttpCall);
        // An SFTP target is one host and one directory; deposit and read are two order kinds
        // against the same declaration, so it is announced under the kind it allows.
        foreach (var t in SftpTargets)
        {
            if (t.AllowPut) yield return (t.Name, RelayOrderKind.SftpPut);
            if (t.AllowRead) yield return (t.Name, RelayOrderKind.SftpRead);
        }
        foreach (var t in SqlViewTargets) yield return (t.Name, RelayOrderKind.SqlView);
    }
}

/// <summary>Where SANKORE is, and the limits of one session.</summary>
public sealed class RelaySankoreOptions
{
    /// <summary>
    /// The WebSocket endpoint to dial, <c>wss://</c> only. Plain <c>ws://</c> is refused by the
    /// validator: this carries an IMF's customer data across whatever network sits between.
    /// </summary>
    public string ChannelUri { get; set; } = string.Empty;

    /// <summary>
    /// Optional SHA-256 thumbprint of the certificate SANKORE must present, lower-case hex, no
    /// colons. Left empty, the operating system's trust store decides — which is the right
    /// default for a public CA. Set, it pins: useful when the IMF's own proxy terminates TLS and
    /// the IT team wants to be sure it is not doing so on this connection.
    /// </summary>
    public string? ServerCertificateThumbprint { get; set; }

    /// <summary>How long the dial and the protocol handshake may take together.</summary>
    public int HandshakeTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Cap on one received frame. A bound and not a nicety: a frame is accumulated in memory
    /// before it is decoded, so an unbounded one is how a single malformed send exhausts a
    /// process running on a branch office's server.
    /// </summary>
    public int MaxFrameBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// How many orders may run at once. Orders run off the receive loop so a slow SFTP deposit
    /// cannot stall the heartbeat; this is what stops a burst from opening forty SSH sessions
    /// into the IMF's file server at the same moment. Beyond it, an order is refused with
    /// <c>RELAY_AGENT_BUSY</c> — refusing fast is better than queueing work SANKORE has already
    /// given up waiting for.
    /// </summary>
    public int MaxConcurrentOrders { get; set; } = 4;
}

/// <summary>
/// The client half of the mutual TLS. Two ways to supply it, because the two deployment shapes
/// want different ones: a container gets a mounted PKCS#12 file, a Windows service can keep the
/// private key in the machine store where it never sits on disk as a readable file.
/// </summary>
public sealed class RelayCertificateOptions
{
    /// <summary>Path to a PKCS#12 (.pfx / .p12) file holding the certificate AND its private key.</summary>
    public string? Pkcs12Path { get; set; }

    /// <summary>
    /// Password for <see cref="Pkcs12Path"/>. Prefer <see cref="PasswordFile"/> or the
    /// environment variable <c>Relay__Certificate__Password</c>: a password in the same file as
    /// the configuration is a password in whatever backs that file up.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Path to a file whose entire content (trimmed) is the password. This is the shape a Docker
    /// or Swarm secret takes, so it is the recommended one in a container.
    /// </summary>
    public string? PasswordFile { get; set; }

    /// <summary>
    /// SHA-256 thumbprint of a certificate to find in the Windows certificate store instead of
    /// loading a file. Mutually exclusive with <see cref="Pkcs12Path"/>.
    /// </summary>
    public string? StoreThumbprint { get; set; }

    /// <summary>Store to search when <see cref="StoreThumbprint"/> is set.</summary>
    public string StoreName { get; set; } = "My";

    /// <summary>Store location to search: <c>LocalMachine</c> or <c>CurrentUser</c>.</summary>
    public string StoreLocation { get; set; } = "LocalMachine";
}

/// <summary>
/// Reconnection policy (criterion 2: "with automatic reconnection").
///
/// <para>
/// The defaults are chosen against a specific failure: SANKORE goes down, and every enrolled
/// agent of every IMF notices within a second. A reconnect loop without a cap would then put a
/// steady, synchronised load on a platform that is already in an incident, and the agents would
/// be a second incident on top of the first. One attempt per minute per agent, spread by jitter,
/// is enough to recover within a minute of the platform coming back and small enough to be
/// invisible while it is down.
/// </para>
/// </summary>
public sealed class RelayReconnectOptions
{
    /// <summary>Delay before the first retry.</summary>
    public int InitialDelaySeconds { get; set; } = 1;

    /// <summary>
    /// The CAP. Doubling stops here, so the loop settles at roughly one attempt a minute no
    /// matter how long the outage lasts.
    /// </summary>
    public int MaxDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Fraction of the delay applied as random spread, each way. Without it every agent that
    /// lost the same SANKORE at the same instant retries at the same instant for ever — the
    /// thundering herd is the reason this field exists, not the backoff.
    /// </summary>
    public double JitterRatio { get; set; } = 0.25;
}

/// <summary>Heartbeat cadence (criterion 5).</summary>
public sealed class RelayHeartbeatOptions
{
    /// <summary>
    /// Seconds between heartbeats. Also the platform's liveness signal, so it has to be short
    /// enough that an operator sees a dead agent in minutes, not hours.
    /// </summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Budget for one target's latency probe. Short: a probe is a TCP connect, and a target that
    /// takes seconds to accept a connection is already the answer the operator needs.
    /// </summary>
    public int ProbeTimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// One declared local HTTP target.
///
/// <para>
/// <see cref="BaseUrl"/> is the agent's authority for this target and the wire cannot alter it:
/// an order supplies a relative path, which is resolved against this base and then checked to
/// still be under it. There is no field here that the wire can set.
/// </para>
/// </summary>
public sealed class RelayHttpTargetOptions
{
    /// <summary>The name orders use. Unique across all target kinds.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute base URL, <c>http://</c> or <c>https://</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Methods an order may use. Defaults to the two read/write verbs a core-banking API
    /// actually needs; a target that should only ever be read is declared with <c>GET</c> alone,
    /// and then no order can write to it whatever SANKORE sends.
    /// </summary>
    public IList<string> AllowedMethods { get; } = ["GET", "POST"];

    /// <summary>
    /// Headers added to every call to this target — typically the local system's API key. They
    /// come from the file because an order that could set a header could set
    /// <c>Authorization</c>.
    /// </summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-call budget.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Cap on the response body held in memory.</summary>
    public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;
}

/// <summary>One declared SFTP target: one host, one directory, one or both directions.</summary>
public sealed class RelaySftpTargetOptions
{
    /// <summary>The name orders use. Unique across all target kinds.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Host name or address, resolved inside the IMF's network.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Port.</summary>
    public int Port { get; set; } = 22;

    /// <summary>SSH user.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Path to an OpenSSH private key. Preferred over <see cref="Password"/>.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Passphrase of <see cref="PrivateKeyPath"/>, or the user's password.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Base64 SHA-256 fingerprint of the server's host key, exactly as <c>ssh-keyscan</c> prints
    /// it (the part after <c>SHA256:</c>).
    ///
    /// <para>
    /// <b>Required</b>, and that is a deliberate refusal to be convenient. Without it the agent
    /// would accept any host key, so anything that can answer on that address inside the IMF's
    /// network can read the files SANKORE deposits and feed it files of its own. The guide
    /// explains how to obtain the value; a missing one fails the boot.
    /// </para>
    /// </summary>
    public string HostKeyFingerprintSha256 { get; set; } = string.Empty;

    /// <summary>
    /// The directory, absolute on the remote host. The only directory this target can touch:
    /// order file names are bare names, validated to carry no path component at all.
    /// </summary>
    public string RemotePath { get; set; } = string.Empty;

    /// <summary>Whether deposits are allowed on this target.</summary>
    public bool AllowPut { get; set; }

    /// <summary>Whether reads are allowed on this target.</summary>
    public bool AllowRead { get; set; }

    /// <summary>Connect and operation budget.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Cap on one file, in either direction, held in memory.</summary>
    public int MaxFileBytes { get; set; } = 16 * 1024 * 1024;
}

/// <summary>
/// One declared, read-only SQL view.
///
/// <para>
/// This is the narrowest target by design and the most important one to read carefully. The
/// agent builds its own statement from <see cref="View"/> and <see cref="Parameters"/>, both of
/// which are validated as SQL identifiers at start-up; the order supplies values, bound as
/// parameters. No part of the statement can come from the wire. A relay that executed SQL it was
/// sent would be a remote-code-execution channel into a bank's database, and no amount of
/// server-side care would make that acceptable.
/// </para>
/// </summary>
public sealed class RelaySqlViewTargetOptions
{
    /// <summary>The name orders use. Unique across all target kinds.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Npgsql connection string. Point it at a role with <c>SELECT</c> on this view and nothing
    /// else — the agent opens a read-only transaction as well, but two locks are better than
    /// one and only the IMF can set the first.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Schema of the view. Separate from <see cref="View"/> so each can be validated as a single
    /// identifier and quoted on its own; a single "schema.view" string would have to be split,
    /// and splitting is where an injection gets in.
    /// </summary>
    public string Schema { get; set; } = "public";

    /// <summary>The view's name, a bare identifier.</summary>
    public string View { get; set; } = string.Empty;

    /// <summary>
    /// Columns an order may filter on. An order naming anything else is refused with
    /// <c>RELAY_PARAMETER_NOT_DECLARED</c>. An empty list means the view takes no filter.
    /// </summary>
    public IList<string> Parameters { get; } = [];

    /// <summary>
    /// Hard row cap, applied as <c>LIMIT</c>. The agent holds the rows in memory to answer, so
    /// this is the bound on that; it is also what keeps a view with a missing filter from
    /// returning a bank's whole customer table over the channel.
    /// </summary>
    public int MaxRows { get; set; } = 200;

    /// <summary>Statement budget.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}
