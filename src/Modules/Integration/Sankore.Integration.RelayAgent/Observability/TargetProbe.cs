namespace Sankore.Integration.RelayAgent.Observability;

using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Sankore.Integration.RelayAgent.Configuration;

/// <summary>
/// Measures the latency towards each declared target, so an idle agent still reports something
/// (criterion 5).
///
/// <para>
/// A TCP connect and nothing more, for all three kinds. That is a deliberate choice over a
/// protocol-level ping:
/// </para>
/// <list type="bullet">
///   <item>
///     uniform — one measurement that means the same thing for an HTTP API, an SFTP server and a
///     database, so the operator compares like with like;
///   </item>
///   <item>
///     side-effect free — an HTTP <c>GET</c> against an unknown path could hit a real endpoint,
///     an SSH handshake every thirty seconds fills the file server's auth log, and a
///     <c>SELECT 1</c> every thirty seconds is a connection per target per heartbeat;
///   </item>
///   <item>
///     silent — it reveals nothing and reads nothing, so there is no payload to accidentally log.
///   </item>
/// </list>
/// <para>
/// What it does NOT prove is that credentials are valid or the view still exists. That is
/// honest: the state field answers "can I reach it", and only a real order answers "does it
/// work" — which is why an executed order's observation overrides a probe's in
/// <see cref="TargetHealthRegistry"/>.
/// </para>
/// </summary>
public sealed class TargetProbe
{
    private readonly RelayAgentOptions _options;
    private readonly TargetHealthRegistry _health;

    public TargetProbe(IOptions<RelayAgentOptions> options, TargetHealthRegistry health)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _health = health;
    }

    /// <summary>
    /// Probes every declared target, in parallel. Parallel because the heartbeat waits on this:
    /// ten targets probed in sequence, each timing out at five seconds, would delay a thirty
    /// second heartbeat past its own interval.
    /// </summary>
    public async Task ProbeAllAsync(CancellationToken cancellationToken)
    {
        var budget = TimeSpan.FromSeconds(_options.Heartbeat.ProbeTimeoutSeconds);
        var probes = new List<Task>();

        foreach (var target in _options.HttpTargets)
        {
            if (Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri))
                probes.Add(ProbeAsync(target.Name, uri.Host, uri.Port, budget, cancellationToken));
        }

        foreach (var target in _options.SftpTargets)
            probes.Add(ProbeAsync(target.Name, target.Host, target.Port, budget, cancellationToken));

        foreach (var target in _options.SqlViewTargets)
        {
            // Parsed from the connection string rather than configured separately: two places to
            // write the same host is two places for them to disagree, and the one the probe used
            // would be the one nobody checked.
            var (host, port) = NpgsqlEndpoint.Parse(target.ConnectionString);
            if (host is not null)
                probes.Add(ProbeAsync(target.Name, host, port, budget, cancellationToken));
        }

        await Task.WhenAll(probes).ConfigureAwait(false);
    }

    private async Task ProbeAsync(
        string name, string host, int port, TimeSpan budget, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(budget);

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

            _health.RecordProbe(name, reached: true, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // We are shutting down. Not an observation about the target, so nothing is recorded —
            // otherwise every stop would leave a trail of false "unreachable" readings.
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException
                                      or ObjectDisposedException)
        {
            _health.RecordProbe(name, reached: false, stopwatch.ElapsedMilliseconds);
        }
    }
}

/// <summary>
/// Pulls host and port out of an Npgsql connection string for the probe, without taking a
/// dependency on parsing the whole thing.
///
/// <para>
/// <c>NpgsqlConnectionStringBuilder</c> would do this properly, and it is used in the executor.
/// It is avoided HERE for one reason: a connection string also holds a password, and building the
/// full object in the probe path would put the credential in one more object graph for one more
/// stack trace to serialise. This reads two keys and keeps the rest unparsed.
/// </para>
/// </summary>
internal static class NpgsqlEndpoint
{
    private const int DefaultPostgresPort = 5432;

    public static (string? Host, int Port) Parse(string connectionString)
    {
        string? host = null;
        var port = DefaultPostgresPort;

        foreach (var pair in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;

            var key = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Server", StringComparison.OrdinalIgnoreCase))
            {
                // A multi-host connection string lists failover hosts; the first is the one a
                // probe can speak about.
                host = value.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            }
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                port = parsed;
            }
        }

        return (host, port);
    }
}
