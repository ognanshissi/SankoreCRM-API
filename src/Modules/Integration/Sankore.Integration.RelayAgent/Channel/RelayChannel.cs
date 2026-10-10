namespace Sankore.Integration.RelayAgent.Channel;

using System.Buffers;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Sankore.Integration.RelayAgent.Configuration;
using Sankore.Integration.RelayAgent.Protocol;

// ---------------------------------------------------------------------------------------------
// OUTBOUND ONLY. This is the whole security argument of the relay agent, and the reason the
// component is shaped like this rather than as a service the platform calls.
//
// SANKORE opens NO port into the IMF's network. The agent dials out, from inside, over a
// connection it initiated, and every order afterwards travels back down that same connection.
// So the IMF's firewall needs no inbound rule, no port forward, no DMZ host, no static address
// and no VPN terminator — a single outbound rule to one hostname on 443, which is a rule their
// network team can grant in an afternoon instead of a change-control cycle.
//
// It also means the attack surface this component adds to the IMF is zero listeners. Nothing can
// reach the agent except the platform it chose to dial and whose certificate it verified.
//
// *** Any design that would require an inbound listener is wrong. *** If a future requirement
// seems to need one — "SANKORE must push urgently", "we need a health endpoint to scrape" — the
// answer is another frame type on this session, not a port. There is no exception to this that
// is worth an inbound rule in a bank's firewall.
//
// WebSocket and not gRPC, decided and not to be re-litigated:
//
//   * A WebSocket over TLS traverses the corporate proxies an IMF actually runs far more
//     reliably. It upgrades from an ordinary HTTP/1.1 request on 443, which every explicit
//     proxy, TLS-inspecting middlebox and legacy load balancer already handles. gRPC needs
//     end-to-end HTTP/2 with prior knowledge or ALPN, and the equipment between a branch office
//     and the internet is where that quietly fails — it fails at the handshake, from inside a
//     network we cannot debug.
//   * The message volume here is low: orders, their answers, and a heartbeat every thirty
//     seconds. gRPC's framing, flow control and streaming buy nothing at this rate.
//   * System.Net.WebSockets.ClientWebSocket is in the BCL, so it adds no package — which matters
//     for a binary the IMF's IT team will be asked to vet.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One session to SANKORE: dialled outbound, authenticated with mutual TLS, carrying JSON frames
/// in both directions. See the file header for why there is no inbound counterpart.
/// </summary>
public sealed class RelayChannel : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly int _maxFrameBytes;

    /// <summary>
    /// Serialises sends. Orders are executed off the receive loop so a slow SFTP deposit cannot
    /// stall the heartbeat, which means several tasks can want to send at once —
    /// <c>ClientWebSocket</c> permits one send and one receive concurrently, and two concurrent
    /// sends corrupt the stream rather than failing.
    /// </summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private RelayChannel(ClientWebSocket socket, int maxFrameBytes)
    {
        _socket = socket;
        _maxFrameBytes = maxFrameBytes;
    }

    /// <summary>
    /// Dials SANKORE and completes the protocol handshake.
    ///
    /// <para>
    /// The handshake is part of connecting, not a first message: a platform that accepts the TCP
    /// connection and then refuses our certificate, or speaks a different protocol version, must
    /// be a failed connection attempt that the backoff counts — not a live session that fails on
    /// its first order.
    /// </para>
    /// </summary>
    public static async Task<RelayChannel> ConnectAsync(
        RelayAgentOptions options,
        X509Certificate2 clientCertificate,
        string agentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var socket = new ClientWebSocket();

        try
        {
            socket.Options.ClientCertificates.Add(clientCertificate);

            // Set explicitly rather than relying on ClientWebSocket's default, which has not
            // always been the environment-aware one. An IMF with a mandatory outbound proxy is the
            // normal case, not the exception, and HttpClient.DefaultProxy is what reads
            // HTTPS_PROXY / ALL_PROXY / NO_PROXY on every platform — the variables the
            // installation guide tells their IT team to set.
            socket.Options.Proxy = System.Net.Http.HttpClient.DefaultProxy;

            // The agent's half of the mutual TLS is the certificate above. The platform's half is
            // verified by the OS trust store by default; a pinned thumbprint replaces that, which
            // an IMF whose own proxy terminates TLS can use to be certain it is not doing so here.
            if (!string.IsNullOrWhiteSpace(options.Sankore.ServerCertificateThumbprint))
            {
                var expected = options.Sankore.ServerCertificateThumbprint;
                socket.Options.RemoteCertificateValidationCallback =
                    (_, certificate, _, _) => MatchesPin(certificate, expected);
            }

            // Not negotiable and not configurable. A deployment that needed to disable
            // certificate validation would be a deployment with no mutual TLS, so there is no
            // switch for it — the next person to need one must change this file in a commit.
            socket.Options.RemoteCertificateValidationCallback ??=
                (_, _, _, errors) => errors == SslPolicyErrors.None;

            using var handshakeTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(
                TimeSpan.FromSeconds(options.Sankore.HandshakeTimeoutSeconds));

            await socket.ConnectAsync(
                new Uri(options.Sankore.ChannelUri), handshakeTimeout.Token).ConfigureAwait(false);

            var channel = new RelayChannel(socket, options.Sankore.MaxFrameBytes);

            var hello = new RelayHello(
                RelayProtocolJson.ProtocolVersion,
                agentVersion,
                options.AllTargets()
                    .Select(t => new RelayDeclaredTarget(t.Name, t.Kind))
                    .ToList());

            await channel.SendAsync(RelayFrameTypes.Hello, hello, handshakeTimeout.Token)
                .ConfigureAwait(false);

            var frame = await channel.ReceiveAsync(handshakeTimeout.Token).ConfigureAwait(false)
                ?? throw new RelayProtocolException(
                    "SANKORE closed the session before answering the handshake.");

            if (frame.Type != RelayFrameTypes.Welcome)
            {
                throw new RelayProtocolException(
                    $"Expected a '{RelayFrameTypes.Welcome}' frame, got '{frame.Type}'.");
            }

            var welcome = frame.Body.Deserialize<RelayWelcome>(RelayProtocolJson.Options);

            if (welcome?.ProtocolVersion != RelayProtocolJson.ProtocolVersion)
            {
                // Refused loudly rather than attempted. A version mismatch that is tolerated
                // becomes fields deserialising to null — the failure mode M02's biometry client
                // spent three retries per file discovering.
                throw new RelayProtocolException(
                    $"Protocol version mismatch: this agent speaks "
                    + $"{RelayProtocolJson.ProtocolVersion}, SANKORE answered "
                    + $"{welcome?.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "nothing"}. "
                    + "Upgrade the agent.");
            }

            return channel;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>True while the session can still carry frames.</summary>
    public bool IsOpen => _socket.State == WebSocketState.Open;

    /// <summary>Puts one frame on the session.</summary>
    public async Task SendAsync<T>(string type, T body, CancellationToken cancellationToken)
    {
        var frame = new RelayFrame(
            type, JsonSerializer.SerializeToElement(body, RelayProtocolJson.Options));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, RelayProtocolJson.Options);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(
                bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Reads one frame, or null when SANKORE closed the session cleanly.
    ///
    /// <para>
    /// Accumulated into a rented buffer with a hard cap: a frame is held in memory before it can
    /// be decoded, so an unbounded one is how a single malformed send exhausts a process running
    /// on a branch office's server. Exceeding the cap ends the session rather than the frame —
    /// the stream's position is no longer trustworthy once we have stopped reading a message
    /// mid-way.
    /// </para>
    /// </summary>
    public async Task<RelayFrame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        using var accumulated = new MemoryStream();

        try
        {
            while (true)
            {
                var received = await _socket.ReceiveAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);

                if (received.MessageType == WebSocketMessageType.Close) return null;

                if (accumulated.Length + received.Count > _maxFrameBytes)
                {
                    throw new RelayProtocolException(
                        "A frame exceeded Relay:Sankore:MaxFrameBytes. The session is closed "
                        + "because the stream can no longer be read in step.");
                }

                accumulated.Write(buffer, 0, received.Count);

                if (received.EndOfMessage) break;
            }

            if (accumulated.Length == 0) return null;

            accumulated.Position = 0;

            return JsonSerializer.Deserialize<RelayFrame>(accumulated, RelayProtocolJson.Options)
                ?? throw new RelayProtocolException("A frame deserialised to null.");
        }
        catch (JsonException ex)
        {
            throw new RelayProtocolException("A frame was not valid JSON.", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool MatchesPin(System.Security.Cryptography.X509Certificates.X509Certificate? certificate, string expectedSha256)
    {
        if (certificate is null) return false;

        using var presented = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var thumbprint = Convert.ToHexStringLower(
            presented.GetCertHash(HashAlgorithmName.SHA256));

        // Constant time: a pin comparison is a secret comparison, and the repository already
        // settled this question for API keys (ApiKeyValidator.Matches).
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(thumbprint),
            System.Text.Encoding.ASCII.GetBytes(expectedSha256.ToLowerInvariant()));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                // A clean close so the platform can tell a deliberate stop from a dead agent, and
                // mark the session closed instead of waiting for its own timeout. Short budget:
                // we are already shutting down, and a server that does not answer the close
                // handshake must not hold the process open.
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "agent stopping", closing.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException
                                      or ObjectDisposedException)
        {
            // Closing a session that is already gone. Nothing to report and nobody to report to.
        }
        finally
        {
            _socket.Dispose();
            _sendLock.Dispose();
        }
    }
}

/// <summary>
/// A frame or a handshake this agent will not accept. Distinct from <c>WebSocketException</c> so
/// the reconnect loop can tell "the network broke" from "the two halves of the protocol
/// disagree" — the first is worth retrying for ever, the second is worth saying out loud.
/// </summary>
public sealed class RelayProtocolException : Exception
{
    public RelayProtocolException() { }

    public RelayProtocolException(string message) : base(message) { }

    public RelayProtocolException(string message, Exception innerException)
        : base(message, innerException) { }
}
