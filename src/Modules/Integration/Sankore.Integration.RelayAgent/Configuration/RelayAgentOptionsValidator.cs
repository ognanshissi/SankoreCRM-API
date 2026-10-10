namespace Sankore.Integration.RelayAgent.Configuration;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

/// <summary>
/// Validated at start-up with <c>ValidateOnStart</c>, so a bad entry is a boot failure naming the
/// key.
///
/// <para>
/// That is worth more here than in most options classes. This agent runs on a machine nobody
/// watches, installed once by an IT team that will not read its logs again; a target that is
/// quietly unusable would surface weeks later as "SANKORE does not see our balances", with the
/// cause in a log file on a branch server. Refusing to start is the only failure mode the IT
/// team cannot miss.
/// </para>
///
/// <para>
/// The identifier checks are not cosmetic. <see cref="SqlIdentifier"/> is what lets
/// <c>SqlViewOrderExecutor</c> interpolate a schema, a view and a column list into a statement
/// at all: every one of those strings has been proven, before the first order, to be a bare SQL
/// identifier. Remove this check and that executor becomes an injection point — against the
/// configuration file rather than the wire, but an injection point.
/// </para>
/// </summary>
internal sealed partial class RelayAgentOptionsValidator : IValidateOptions<RelayAgentOptions>
{
    private const int MaxTimeoutSeconds = 300;

    /// <summary>
    /// A bare, unquoted-safe SQL identifier. No dots, no quotes, no spaces: the quoting in the
    /// executor is then belt to this brace.
    /// </summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SqlIdentifier();

    /// <summary>A target name: operator-facing, used as a log field and a dictionary key.</summary>
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetName();

    private static readonly string[] KnownHttpMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    public ValidateOptionsResult Validate(string? name, RelayAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var root = RelayAgentOptions.SectionName;

        ValidateSankore(options.Sankore, $"{root}:Sankore", failures);
        ValidateCertificate(options.Certificate, $"{root}:Certificate", failures);
        ValidateReconnect(options.Reconnect, $"{root}:Reconnect", failures);
        ValidateHeartbeat(options.Heartbeat, $"{root}:Heartbeat", failures);

        // One flat name space across the three kinds: an order carries a name and a kind, and a
        // name meaning two things would make which one it reached depend on enumeration order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < options.HttpTargets.Count; i++)
            ValidateHttpTarget(options.HttpTargets[i], $"{root}:HttpTargets:{i}", seen, failures);

        for (var i = 0; i < options.SftpTargets.Count; i++)
            ValidateSftpTarget(options.SftpTargets[i], $"{root}:SftpTargets:{i}", seen, failures);

        for (var i = 0; i < options.SqlViewTargets.Count; i++)
            ValidateSqlTarget(options.SqlViewTargets[i], $"{root}:SqlViewTargets:{i}", seen, failures);

        // Not an error. An agent with no target is a legitimate first install — enrol it, see the
        // session come up, then declare the systems. A warning would be invisible, so the guide
        // covers it and the heartbeat reports zero targets, which is visible.

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSankore(
        RelaySankoreOptions o, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(o.ChannelUri))
        {
            failures.Add($"{key}:ChannelUri is required — the agent has nowhere to dial.");
        }
        else if (!Uri.TryCreate(o.ChannelUri, UriKind.Absolute, out var uri))
        {
            failures.Add($"{key}:ChannelUri is not an absolute URI.");
        }
        else if (!string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            // ws:// would carry an IMF's customer data in clear and could not carry a client
            // certificate at all, so mutual TLS — criterion 2 — would be silently absent.
            failures.Add($"{key}:ChannelUri must use wss:// (mutual TLS), not '{uri.Scheme}'.");
        }

        if (!string.IsNullOrWhiteSpace(o.ServerCertificateThumbprint)
            && !IsSha256Hex(o.ServerCertificateThumbprint))
        {
            failures.Add(
                $"{key}:ServerCertificateThumbprint must be 64 hexadecimal characters "
                + "(SHA-256, no colons).");
        }

        if (o.HandshakeTimeoutSeconds is < 1 or > MaxTimeoutSeconds)
            failures.Add($"{key}:HandshakeTimeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");

        if (o.MaxFrameBytes < 1024)
            failures.Add($"{key}:MaxFrameBytes must be at least 1024.");

        if (o.MaxConcurrentOrders is < 1 or > 64)
            failures.Add($"{key}:MaxConcurrentOrders must be between 1 and 64.");
    }

    private static void ValidateCertificate(
        RelayCertificateOptions o, string key, List<string> failures)
    {
        var hasFile = !string.IsNullOrWhiteSpace(o.Pkcs12Path);
        var hasStore = !string.IsNullOrWhiteSpace(o.StoreThumbprint);

        if (hasFile == hasStore)
        {
            // Both or neither. "Neither" means no client certificate, so no mutual TLS; "both"
            // means the agent would pick one and the IT team would not know which — and the one
            // they rotated might be the other.
            failures.Add(
                $"{key}: set exactly one of Pkcs12Path (a mounted .pfx) or StoreThumbprint "
                + "(a certificate already in the Windows store).");
        }

        if (hasStore && !IsSha256Hex(o.StoreThumbprint))
            failures.Add($"{key}:StoreThumbprint must be 64 hexadecimal characters (SHA-256).");

        if (hasStore
            && !string.Equals(o.StoreLocation, "LocalMachine", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(o.StoreLocation, "CurrentUser", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{key}:StoreLocation must be 'LocalMachine' or 'CurrentUser'.");
        }

        if (!string.IsNullOrWhiteSpace(o.Password) && !string.IsNullOrWhiteSpace(o.PasswordFile))
            failures.Add($"{key}: set Password or PasswordFile, not both.");
    }

    private static void ValidateReconnect(
        RelayReconnectOptions o, string key, List<string> failures)
    {
        if (o.InitialDelaySeconds is < 1 or > 60)
            failures.Add($"{key}:InitialDelaySeconds must be between 1 and 60.");

        if (o.MaxDelaySeconds < o.InitialDelaySeconds)
            failures.Add($"{key}:MaxDelaySeconds cannot be below InitialDelaySeconds.");

        // A cap above ten minutes makes recovery after a long outage depend on luck: the platform
        // comes back and the agent is asleep for another nine minutes, which reads as the agent
        // being dead.
        if (o.MaxDelaySeconds > 600)
            failures.Add($"{key}:MaxDelaySeconds must not exceed 600.");

        if (o.JitterRatio is < 0 or > 1)
            failures.Add($"{key}:JitterRatio must be between 0 and 1.");
    }

    private static void ValidateHeartbeat(
        RelayHeartbeatOptions o, string key, List<string> failures)
    {
        if (o.IntervalSeconds is < 5 or > 600)
            failures.Add($"{key}:IntervalSeconds must be between 5 and 600.");

        if (o.ProbeTimeoutSeconds is < 1 or > 30)
            failures.Add($"{key}:ProbeTimeoutSeconds must be between 1 and 30.");

        if (o.ProbeTimeoutSeconds >= o.IntervalSeconds)
        {
            // Probes run before each heartbeat; a probe budget at or above the interval means the
            // heartbeats pile up behind their own probes and the cadence stops being the cadence.
            failures.Add($"{key}:ProbeTimeoutSeconds must be below IntervalSeconds.");
        }
    }

    private static void ValidateHttpTarget(
        RelayHttpTargetOptions o, string key, HashSet<string> seen, List<string> failures)
    {
        ValidateName(o.Name, key, seen, failures);

        if (!Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{key}:BaseUrl must be an absolute http:// or https:// URL.");
        }
        else if (!o.BaseUrl.EndsWith('/'))
        {
            // Uri.TryCreate(base, relative) drops the last segment of a base that does not end
            // in a slash, so "https://cbs/api" + "accounts" resolves to "https://cbs/accounts".
            // Refused rather than silently fixed: the IT team should see the address the agent
            // will actually use written in their own file.
            failures.Add(
                $"{key}:BaseUrl must end with '/', or relative paths resolve one segment too high.");
        }

        if (o.AllowedMethods.Count == 0)
            failures.Add($"{key}:AllowedMethods must list at least one method.");

        foreach (var method in o.AllowedMethods)
        {
            if (!KnownHttpMethods.Contains(method.ToUpperInvariant(), StringComparer.Ordinal))
            {
                failures.Add(
                    $"{key}:AllowedMethods contains '{method}', which is not one of "
                    + string.Join(", ", KnownHttpMethods) + ".");
            }
        }

        if (o.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
            failures.Add($"{key}:TimeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");

        if (o.MaxResponseBytes < 1024)
            failures.Add($"{key}:MaxResponseBytes must be at least 1024.");
    }

    private static void ValidateSftpTarget(
        RelaySftpTargetOptions o, string key, HashSet<string> seen, List<string> failures)
    {
        ValidateName(o.Name, key, seen, failures);

        if (string.IsNullOrWhiteSpace(o.Host))
            failures.Add($"{key}:Host is required.");

        if (o.Port is < 1 or > 65535)
            failures.Add($"{key}:Port must be between 1 and 65535.");

        if (string.IsNullOrWhiteSpace(o.Username))
            failures.Add($"{key}:Username is required.");

        if (string.IsNullOrWhiteSpace(o.PrivateKeyPath) && string.IsNullOrWhiteSpace(o.Password))
            failures.Add($"{key}: one of PrivateKeyPath or Password is required.");

        if (string.IsNullOrWhiteSpace(o.HostKeyFingerprintSha256))
        {
            failures.Add(
                $"{key}:HostKeyFingerprintSha256 is required. Obtain it with "
                + "`ssh-keyscan -t rsa,ed25519 <host> | ssh-keygen -lf -` and copy the part "
                + "after 'SHA256:'. Without it the agent would trust any host answering on "
                + "that address.");
        }

        if (string.IsNullOrWhiteSpace(o.RemotePath) || !o.RemotePath.StartsWith('/'))
            failures.Add($"{key}:RemotePath is required and must be absolute (start with '/').");

        if (!o.AllowPut && !o.AllowRead)
            failures.Add($"{key}: set AllowPut, AllowRead, or both — this target can do nothing.");

        if (o.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
            failures.Add($"{key}:TimeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");

        if (o.MaxFileBytes < 1024)
            failures.Add($"{key}:MaxFileBytes must be at least 1024.");
    }

    private static void ValidateSqlTarget(
        RelaySqlViewTargetOptions o, string key, HashSet<string> seen, List<string> failures)
    {
        ValidateName(o.Name, key, seen, failures);

        if (string.IsNullOrWhiteSpace(o.ConnectionString))
            failures.Add($"{key}:ConnectionString is required.");

        if (!SqlIdentifier().IsMatch(o.Schema))
            failures.Add($"{key}:Schema must be a bare SQL identifier (letters, digits, '_').");

        if (!SqlIdentifier().IsMatch(o.View))
        {
            failures.Add(
                $"{key}:View must be a bare SQL identifier — put the schema in Schema, not here. "
                + "This is what makes the generated statement safe to build at all.");
        }

        foreach (var parameter in o.Parameters)
        {
            if (!SqlIdentifier().IsMatch(parameter))
                failures.Add($"{key}:Parameters contains '{parameter}', not a bare SQL identifier.");
        }

        if (o.Parameters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != o.Parameters.Count)
            failures.Add($"{key}:Parameters lists the same column twice.");

        if (o.MaxRows is < 1 or > 10_000)
            failures.Add($"{key}:MaxRows must be between 1 and 10000.");

        if (o.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
            failures.Add($"{key}:TimeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");
    }

    private static void ValidateName(
        string name, string key, HashSet<string> seen, List<string> failures)
    {
        if (!TargetName().IsMatch(name))
        {
            failures.Add(
                $"{key}:Name must be 1-64 characters of letters, digits, '.', '_' or '-'. "
                + "It is a log field and a wire value, so it stays printable and non-personal.");
            return;
        }

        if (!seen.Add(name))
            failures.Add($"{key}:Name '{name}' is declared more than once, across all target kinds.");
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64) return false;

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }

        return true;
    }
}
