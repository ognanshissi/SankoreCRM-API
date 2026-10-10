namespace Sankore.Integration.RelayAgent.Channel;

using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Integration.RelayAgent.Configuration;
using Sankore.Integration.RelayAgent.Execution;
using Sankore.Integration.RelayAgent.Observability;
using Sankore.Integration.RelayAgent.Protocol;

/// <summary>
/// The agent's only long-running task: dial SANKORE, serve orders until the session ends, back
/// off, dial again (criterion 2's automatic reconnection).
///
/// <para>
/// Three things run inside one session and the shape is deliberate:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>the receive loop</b>, which reads frames and nothing else. Orders are handed to a
///     background task rather than awaited here, because an SFTP deposit of a large file would
///     otherwise stop the agent reading — including stopping it noticing that SANKORE closed the
///     session;
///   </item>
///   <item>
///     <b>the heartbeat loop</b>, on its own cadence. It must keep reporting while an order is in
///     flight: an agent that went silent whenever it was busy would be indistinguishable from one
///     that had died, and busy is exactly when an operator looks;
///   </item>
///   <item>
///     <b>a concurrency limit</b> on orders, so a burst cannot open forty SSH sessions into the
///     IMF's file server at once. Past the limit an order is refused immediately rather than
///     queued — SANKORE has a budget for it, and work it has given up waiting for should not
///     still be running inside a bank.
///   </item>
/// </list>
///
/// <para>
/// Nothing is kept across a session. No queue of unsent results, no spool of pending orders: an
/// order whose answer could not be delivered is lost, and the platform's own command lifecycle
/// (INT-05/INT-06, idempotency key, retry schedule) is what makes that safe. Buffering here would
/// be a second, worse copy of that machinery, holding a bank's data on a branch server to do it —
/// the opposite of criterion 4.
/// </para>
/// </summary>
public sealed class RelayChannelWorker : BackgroundService
{
    private readonly RelayAgentOptions _options;
    private readonly RelayOrderDispatcher _dispatcher;
    private readonly TargetHealthRegistry _health;
    private readonly TargetProbe _probe;
    private readonly ILogger<RelayChannelWorker> _logger;
    private readonly string _agentVersion;

    private int _ordersInFlight;

    public RelayChannelWorker(
        IOptions<RelayAgentOptions> options,
        RelayOrderDispatcher dispatcher,
        TargetHealthRegistry health,
        TargetProbe probe,
        ILogger<RelayChannelWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _dispatcher = dispatcher;
        _health = health;
        _probe = probe;
        _logger = logger;
        _agentVersion = ResolveAgentVersion();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Loaded once, outside the loop: a certificate the IT team replaces on disk takes effect
        // on the next restart, which is a restart they perform knowingly. A load failure here
        // stops the service, which is right — an agent with no identity has nothing to do.
        using var certificate = ClientCertificateLoader.Load(_options.Certificate);

        var backoff = new ReconnectBackoff(_options.Reconnect);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(certificate, stoppingToken).ConfigureAwait(false);

                // A session that ended without throwing is SANKORE closing it cleanly — a
                // deploy, a scale-down. Not a fault, so the backoff starts from the bottom.
                backoff.Reset();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsSessionFailure(ex))
            {
                var delay = backoff.Next();
                RelayLog.SessionLost(_logger, ex.GetType().Name, delay, backoff.Attempt);

                // A protocol mismatch will not fix itself by retrying, so it is the one failure
                // whose own message is worth printing — it names the action (upgrade the agent)
                // and contains no relayed data, being built from two version numbers.
                if (ex is RelayProtocolException)
                    _logger.LogError("Relay handshake refused: {Reason}", ex.Message);

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                await Task.Delay(backoff.Next(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunSessionAsync(
        X509Certificate2 certificate, CancellationToken stoppingToken)
    {
        await using var channel = await RelayChannel
            .ConnectAsync(_options, certificate, _agentVersion, stoppingToken)
            .ConfigureAwait(false);

        RelayLog.SessionOpened(_logger, new Uri(_options.Sankore.ChannelUri), _agentVersion);

        // Linked so that whichever of the two loops ends first takes the other down with it: a
        // heartbeat loop still sending into a dead session would keep the process looking alive
        // while nothing is being served.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        using var orderSlots = new SemaphoreSlim(
            _options.Sankore.MaxConcurrentOrders, _options.Sankore.MaxConcurrentOrders);

        var receiving = ReceiveLoopAsync(channel, orderSlots, session);
        var beating = HeartbeatLoopAsync(channel, session);

        try
        {
            await Task.WhenAny(receiving, beating).ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);

            // Awaited, not abandoned: an unobserved faulted Task is how the real reason a session
            // died gets swallowed, and the loop above decides its backoff from that reason.
            await Task.WhenAll(Quietly(receiving), Quietly(beating)).ConfigureAwait(false);
        }

        // Rethrows the first genuine failure, now that both loops have stopped.
        await receiving.ConfigureAwait(false);
        await beating.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(
        RelayChannel channel, SemaphoreSlim orderSlots, CancellationTokenSource session)
    {
        while (!session.IsCancellationRequested && channel.IsOpen)
        {
            var frame = await channel.ReceiveAsync(session.Token).ConfigureAwait(false);

            // Null is a clean close by SANKORE. Returning ends the session without an exception,
            // which is what resets the backoff.
            if (frame is null) return;

            if (frame.Type != RelayFrameTypes.Order)
            {
                // Declined, session kept. A platform a version ahead may send a frame type this
                // build does not know, and dropping the connection over it would make every
                // agent in the field unusable the moment the platform gained a feature.
                RelayLog.FrameDeclined(_logger, frame.Type);
                continue;
            }

            RelayOrder? order;
            try
            {
                order = frame.Body.Deserialize<RelayOrder>(RelayProtocolJson.Options);
            }
            catch (JsonException)
            {
                // No correlation id, so there is nobody to answer. Logged as a declined frame
                // rather than as a failed order, which is what it is.
                RelayLog.FrameDeclined(_logger, frame.Type);
                continue;
            }

            if (order is null || string.IsNullOrWhiteSpace(order.CorrelationId))
            {
                RelayLog.FrameDeclined(_logger, frame.Type);
                continue;
            }

            if (!await orderSlots.WaitAsync(TimeSpan.Zero, session.Token).ConfigureAwait(false))
            {
                await SendResultAsync(
                    channel,
                    new RelayOrderResult(
                        order.CorrelationId, order.Kind, RelayOutcome.Refused,
                        RelayErrorCodes.AgentBusy, 0, null),
                    session.Token).ConfigureAwait(false);

                continue;
            }

            // Not awaited: see the type comment. Everything inside is wrapped, so this task
            // cannot fault and cannot fail to release its slot.
            _ = ProcessOrderAsync(channel, order, orderSlots, session.Token);
        }
    }

    private async Task ProcessOrderAsync(
        RelayChannel channel,
        RelayOrder order,
        SemaphoreSlim orderSlots,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _ordersInFlight);

        try
        {
            var result = await _dispatcher.DispatchAsync(order, cancellationToken)
                .ConfigureAwait(false);

            await SendResultAsync(channel, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException
                                      or ObjectDisposedException)
        {
            // The session died while we were answering. The order's answer is lost and the
            // platform's own retry schedule is what recovers it; nothing is spooled here.
            _logger.LogWarning(
                "Relay result could not be delivered, session gone. "
                + "correlationId={CorrelationId} exceptionType={ExceptionType}",
                order.CorrelationId, ex.GetType().Name);
        }
        finally
        {
            Interlocked.Decrement(ref _ordersInFlight);
            orderSlots.Release();
        }
    }

    private static Task SendResultAsync(
        RelayChannel channel, RelayOrderResult result, CancellationToken cancellationToken)
        => channel.SendAsync(RelayFrameTypes.Result, result, cancellationToken);

    private async Task HeartbeatLoopAsync(RelayChannel channel, CancellationTokenSource session)
    {
        var interval = TimeSpan.FromSeconds(_options.Heartbeat.IntervalSeconds);
        var declared = _options.AllTargets().ToList();

        // The first heartbeat goes out immediately, before the first probe: the platform should
        // see an agent's version within a second of it connecting, and waiting a full interval to
        // say so makes an install look like a failed install.
        while (!session.IsCancellationRequested)
        {
            var heartbeat = new RelayHeartbeat(
                _agentVersion,
                _health.OverallState(declared),
                DateTimeOffset.UtcNow,
                Volatile.Read(ref _ordersInFlight),
                _health.Snapshot(declared));

            await channel.SendAsync(RelayFrameTypes.Heartbeat, heartbeat, session.Token)
                .ConfigureAwait(false);

            RelayLog.HeartbeatSent(
                _logger, heartbeat.State, declared.Count, heartbeat.OrdersInFlight);

            await Task.Delay(interval, session.Token).ConfigureAwait(false);

            // Probed AFTER the wait and before the next send, so each heartbeat carries a
            // measurement taken moments earlier rather than one from a whole interval ago.
            await _probe.ProbeAllAsync(session.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Which failures mean "the session ended, dial again". Everything a network, a proxy, a TLS
    /// stack or the protocol can do belongs here; a bug in this project does not, and should stop
    /// the service rather than loop silently for ever.
    /// </summary>
    private static bool IsSessionFailure(Exception exception)
        => exception is WebSocketException
            or RelayProtocolException
            or SocketException
            or IOException
            or System.Security.Authentication.AuthenticationException
            or OperationCanceledException
            or ObjectDisposedException;

    /// <summary>
    /// Awaits a task and swallows its outcome, so both loops can be allowed to finish before the
    /// real failure is rethrown from the one that produced it.
    /// </summary>
    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Observed on purpose. The caller rethrows from the original task straight after.
        }
    }

    /// <summary>
    /// The version the heartbeat reports (criterion 5). Informational version first, because that
    /// is the one carrying a build suffix an operator can match against a release; the assembly
    /// version is the fallback.
    /// </summary>
    private static string ResolveAgentVersion()
    {
        var assembly = typeof(RelayChannelWorker).Assembly;

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "0.0.0";
    }
}
