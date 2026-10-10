namespace Sankore.Integration.RelayAgent.Execution;

using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sankore.Integration.RelayAgent.Configuration;
using Sankore.Integration.RelayAgent.Observability;
using Sankore.Integration.RelayAgent.Protocol;

/// <summary>
/// Criterion 3, the SFTP deposit and read.
///
/// <para>
/// <b>The host, the credentials and the directory come from the configuration file.</b> An order
/// supplies a bare file name, which <see cref="OrderBody.IsSafeFileName"/> proves carries no path
/// component at all — so the directory the agent touches is the declared one, and no sequence of
/// dots, slashes or encodings in the order can move it. Direction is declared too: a drop-box
/// target declared <c>AllowPut</c> only cannot be read back, which is how an IMF keeps SANKORE
/// from collecting the files other systems leave in the same directory.
/// </para>
///
/// <para>
/// <b>The host key is verified against a fingerprint from the file, and a missing fingerprint
/// fails the boot.</b> Inside a corporate network this is the check that is usually skipped — the
/// server is "ours", after all — and it is exactly the check that matters here: this connection
/// carries customer files, and anything that can answer on that address can otherwise read them
/// and feed us files of its own. A mismatch is reported as its own code
/// (<c>RELAY_HOST_KEY_REFUSED</c>) rather than as an outage, because the two want opposite
/// reactions from whoever reads it.
/// </para>
///
/// <para>
/// A connection per order, not pooled. A pool would be faster and would hold an authenticated
/// SSH session into a bank's file server open between orders, which is a standing capability
/// rather than a used one; the order rate here is low enough that the handshake is affordable.
/// Criterion 4 points the same way: nothing is retained between orders, sessions included.
/// </para>
/// </summary>
public sealed class SftpOrderExecutor : IRelayOrderExecutor
{
    private readonly Dictionary<string, RelaySftpTargetOptions> _targets;
    private readonly RelayOrderKind _kind;

    public SftpOrderExecutor(IOptions<RelayAgentOptions> options, RelayOrderKind kind)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (kind is not (RelayOrderKind.SftpPut or RelayOrderKind.SftpRead))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an SFTP order kind.");

        _kind = kind;
        _targets = options.Value.SftpTargets.ToDictionary(
            t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public RelayOrderKind Kind => _kind;

    public async Task<RelayExecution> ExecuteAsync(
        RelayOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!_targets.TryGetValue(order.Target, out var target))
            return RelayExecution.Refuse(RelayErrorCodes.TargetNotDeclared);

        var allowed = _kind == RelayOrderKind.SftpPut ? target.AllowPut : target.AllowRead;
        if (!allowed)
            return RelayExecution.Refuse(RelayErrorCodes.DirectionNotAllowed);

        var budget = TimeSpan.FromSeconds(
            order.TimeoutSeconds is > 0 and var requested && requested < target.TimeoutSeconds
                ? requested
                : target.TimeoutSeconds);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);

        // Set by the host-key handler, read after the connect attempt fails. The handler cannot
        // report a result itself — SSH.NET answers a refused key by throwing a generic
        // SshConnectionException, which is indistinguishable from the server being down.
        var hostKeyRefused = false;

        try
        {
            using var client = new SftpClient(BuildConnectionInfo(target, budget));

            client.HostKeyReceived += (_, e) =>
            {
                // FingerPrintSHA256 is base64 without padding, the same form `ssh-keygen -lf`
                // prints after "SHA256:". Compared with Ordinal and with the padding that an
                // operator may have pasted trimmed off either side.
                var presented = e.FingerPrintSHA256?.TrimEnd('=') ?? string.Empty;
                var declared = target.HostKeyFingerprintSha256.Trim().TrimEnd('=');

                e.CanTrust = string.Equals(presented, declared, StringComparison.Ordinal);
                if (!e.CanTrust) hostKeyRefused = true;
            };

            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);

            var result = _kind == RelayOrderKind.SftpPut
                ? await PutAsync(client, target, order, timeout.Token).ConfigureAwait(false)
                : await ReadAsync(client, target, order, timeout.Token).ConfigureAwait(false);

            client.Disconnect();
            return result;
        }
        catch (SshConnectionException ex) when (hostKeyRefused)
        {
            return RelayExecution.Unavailable(
                RelayErrorCodes.HostKeyRefused, ex.GetType().Name, reachedTarget: true);
        }
        catch (SshAuthenticationException ex)
        {
            // Reached: the server answered and refused us. An operator told "unreachable" would
            // check the network; the credential is what needs checking.
            return RelayExecution.Unavailable(
                RelayErrorCodes.TargetError, ex.GetType().Name, reachedTarget: true);
        }
        catch (SftpPathNotFoundException ex)
        {
            return RelayExecution.Unavailable(
                RelayErrorCodes.TargetError, ex.GetType().Name, reachedTarget: true);
        }
        catch (SftpPermissionDeniedException ex)
        {
            return RelayExecution.Unavailable(
                RelayErrorCodes.TargetError, ex.GetType().Name, reachedTarget: true);
        }
        catch (SftpException ex)
        {
            return RelayExecution.Unavailable(
                RelayErrorCodes.TargetError, ex.GetType().Name, reachedTarget: true);
        }
        catch (SshOperationTimeoutException ex)
        {
            return RelayExecution.Unavailable(RelayErrorCodes.TargetTimeout, ex.GetType().Name);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RelayExecution.Unavailable(
                RelayErrorCodes.TargetTimeout, nameof(OperationCanceledException));
        }
        catch (SshException ex)
        {
            return RelayExecution.Unavailable(RelayErrorCodes.TargetUnreachable, ex.GetType().Name);
        }
        catch (SocketException ex)
        {
            return RelayExecution.Unavailable(RelayErrorCodes.TargetUnreachable, ex.GetType().Name);
        }
        catch (Exception ex) when (ex is IOException or ProxyException or FormatException)
        {
            // FormatException covers a private key file the library cannot parse — a
            // configuration fault, but one that can only be discovered on first use.
            return RelayExecution.Unexpected(ex);
        }
    }

    private static async Task<RelayExecution> PutAsync(
        SftpClient client,
        RelaySftpTargetOptions target,
        RelayOrder order,
        CancellationToken cancellationToken)
    {
        if (!OrderBody.TryRead<RelaySftpPutBody>(order, out var body) || body is null)
            return RelayExecution.Refuse(RelayErrorCodes.FrameInvalid);

        if (!OrderBody.IsSafeFileName(body.FileName))
            return RelayExecution.Refuse(RelayErrorCodes.FileNameInvalid);

        byte[] content;
        try
        {
            content = Convert.FromBase64String(body.ContentBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            return RelayExecution.Refuse(RelayErrorCodes.FrameInvalid);
        }

        if (content.Length > target.MaxFileBytes)
            return RelayExecution.Refuse(RelayErrorCodes.PayloadTooLarge);

        using var source = new MemoryStream(content, writable: false);
        var remote = Combine(target.RemotePath, body.FileName);

        await client.UploadFileAsync(source, remote, cancellationToken).ConfigureAwait(false);

        var payload = new RelaySftpPutResult(body.FileName, content.Length);
        return RelayExecution.Ok(
            JsonSerializer.SerializeToElement(payload, RelayProtocolJson.Options));
    }

    private static async Task<RelayExecution> ReadAsync(
        SftpClient client,
        RelaySftpTargetOptions target,
        RelayOrder order,
        CancellationToken cancellationToken)
    {
        if (!OrderBody.TryRead<RelaySftpReadBody>(order, out var body) || body is null)
            return RelayExecution.Refuse(RelayErrorCodes.FrameInvalid);

        if (!OrderBody.IsSafeFileName(body.FileName))
            return RelayExecution.Refuse(RelayErrorCodes.FileNameInvalid);

        var remote = Combine(target.RemotePath, body.FileName);

        // Size first, so an unexpectedly huge file is refused before a byte of it is transferred
        // across the IMF's network and into this process's memory. DownloadFileAsync writes into
        // the stream we give it and offers no cap of its own.
        var attributes = await client.GetAttributesAsync(remote, cancellationToken)
            .ConfigureAwait(false);

        if (attributes.Size > target.MaxFileBytes)
            return RelayExecution.Unavailable(RelayErrorCodes.PayloadTooLarge, reachedTarget: true);

        using var destination = new MemoryStream();
        await client.DownloadFileAsync(remote, destination, cancellationToken).ConfigureAwait(false);

        var bytes = destination.ToArray();
        var payload = new RelaySftpReadResult(
            body.FileName, Convert.ToBase64String(bytes), bytes.Length);

        return RelayExecution.Ok(
            JsonSerializer.SerializeToElement(payload, RelayProtocolJson.Options));
    }

    /// <summary>
    /// Joins the declared directory and a validated bare file name. Not <c>Path.Combine</c>: that
    /// one is host-relative, so on a Windows service it would produce a backslash for a POSIX
    /// SFTP server, and if the name ever began with a separator it would discard the directory.
    /// </summary>
    private static string Combine(string remotePath, string fileName)
        => remotePath.TrimEnd('/') + '/' + fileName;

    private static ConnectionInfo BuildConnectionInfo(
        RelaySftpTargetOptions target, TimeSpan budget)
    {
        AuthenticationMethod authentication;

        if (!string.IsNullOrWhiteSpace(target.PrivateKeyPath))
        {
            var key = string.IsNullOrEmpty(target.Password)
                ? new PrivateKeyFile(target.PrivateKeyPath)
                : new PrivateKeyFile(target.PrivateKeyPath, target.Password);

            authentication = new PrivateKeyAuthenticationMethod(target.Username, key);
        }
        else
        {
            authentication = new PasswordAuthenticationMethod(target.Username, target.Password);
        }

        return new ConnectionInfo(target.Host, target.Port, target.Username, authentication)
        {
            // SSH.NET's own timeout, in addition to the cancellation token: the token covers the
            // awaits, this covers the library's internal blocking waits during the handshake.
            Timeout = budget,
        };
    }
}
