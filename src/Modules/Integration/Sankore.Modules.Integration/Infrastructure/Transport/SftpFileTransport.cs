namespace Sankore.Modules.Integration.Infrastructure.Transport;

using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// <see cref="IIntegrationFileTransport"/> over SSH.NET — the DIRECT half of INT-24 criterion 4,
/// where this process opens the outbound connection itself.
///
/// <para>
/// <b>Host key verification is not optional and there is no "trust on first use".</b> The event
/// handler below starts from <c>CanTrust = false</c> and only ever raises it on a match against
/// the fingerprint in the vault; a connection attempted with no fingerprint configured is refused
/// before a socket is opened. An SFTP client that accepts any host key is one DNS answer away
/// from handing a bank's whole customer file — names, identity-document numbers, declared income
/// — to whoever answers on port 22, and the deposit would still look successful in every log.
/// So the failure mode is a refusal an administrator has to fix, never a silent downgrade.
/// </para>
///
/// <para>
/// The fingerprint lives in <c>IntegrationSecrets.SftpHostKeyFingerprintKey</c> rather than on
/// <c>BatchCapableSettings</c>: that settings object is returned by the API, so a host key a
/// caller could write is a host key a caller could replace. See the constant's own comment.
/// </para>
///
/// <para>
/// Nothing here throws at the caller. A host that is down, a directory that does not exist, a key
/// the server refuses — each is an <see cref="IntegrationResult"/> whose family decides whether
/// the batch job retries, exactly as the seam's documentation requires. The mapping is the
/// module's standard reading: our own misconfiguration is <c>Technical</c>, the far end being
/// unreachable is <c>Transient</c>.
/// </para>
/// </summary>
internal sealed class SftpFileTransport(
    ISecretsModule secrets,
    SftpEgressGuard egress,
    ILogger<SftpFileTransport> logger) : IIntegrationFileTransport
{
    /// <summary>
    /// The ONE sentence every network-decided failure answers with — a blocked address, a host
    /// that does not resolve, a refused connection, a timeout, a host key that does not match.
    ///
    /// <para>
    /// Collapsed on purpose. Those outcomes are distinguishable only to somebody choosing the
    /// address, and <c>SftpHost</c> is tenant-editable: three different answers turn the deposit
    /// into a scanner that maps SANKORE's internal network one address at a time, which is the
    /// real prize once connecting is blocked. The distinguishing detail goes to the server log,
    /// at Error, with the connection id — an operator has it in full, and a caller has nothing.
    /// </para>
    ///
    /// <para>
    /// The family is collapsed with it (<c>Transient</c>, <see cref="IntegrationErrors.Unavailable"/>):
    /// leaving a host-key mismatch as <c>Technical</c> while a timeout stayed <c>Transient</c>
    /// would rebuild the same oracle out of <c>last_error_family</c>.
    /// </para>
    ///
    /// <para>
    /// What stays distinguishable is everything decided BEFORE a socket — a missing credential, a
    /// missing fingerprint, an unconfigured directory — because those describe configuration the
    /// administrator wrote and reveal nothing about what is reachable; and everything decided
    /// AFTER a verified session (authentication refused, permission denied, a path absent on the
    /// server), because reaching those already required a host key matching the fingerprint only
    /// an administrator could have stored. The host-key check is what gates that oracle, which is
    /// why the two protections are independent and both needed.
    /// </para>
    /// </summary>
    private const string UnreachableDetail =
        "A verified SFTP session with the configured server could not be established. The cause "
        + "is in the server log for this connection.";

    /// <summary>
    /// Suffix a file carries while it is being written. The CBS polls the deposit directory, and
    /// a file that appears under its final name the moment the first byte lands is a file the CBS
    /// may read half-written — which for a batch file means a partial day applied, with a
    /// checksum that will never match. The rename is atomic on every POSIX server.
    /// </summary>
    private const string PartialSuffix = ".part";

    /// <summary>Where a processed inbound file is moved. Created on demand.</summary>
    private const string ArchiveFolder = "archive";

    /// <summary>
    /// Floor and ceiling of the per-operation budget. A file transfer is not an API call:
    /// <c>TimeoutSeconds</c> is tuned for the latter (and capped far below this by the adapters),
    /// so a connection configured for a 5-second REST budget would abort every deposit. Ten
    /// seconds is the floor, five minutes the ceiling — past which a stalled transfer should be
    /// retried rather than held, so the Hangfire worker is given back.
    /// </summary>
    private static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public async Task<IntegrationResult> PutAsync(
        IntegrationConnection connection, string fileName, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(content);

        var settings = BatchSettings(connection);
        if (settings is null) return MissingBatchSettings(connection);

        if (string.IsNullOrWhiteSpace(settings.OutboundDirectory))
            return IntegrationResult.Technical(
                IntegrationErrors.SettingsInvalid,
                "No OutboundDirectory is configured on this connection; there is nowhere to "
                + "deposit the file.");

        if (!IsSafeFileName(fileName))
            return IntegrationResult.Technical(
                IntegrationErrors.SettingsInvalid,
                "The generated file name is not a single safe path segment.");

        var result = await WithClientAsync(connection, settings, async (client, token) =>
        {
            var finalPath = Join(settings.OutboundDirectory!, fileName);
            var partialPath = finalPath + PartialSuffix;

            // A leftover .part is a previous attempt that died mid-transfer. Removing it is safe
            // precisely because the name is reserved for an in-flight write of OUR file: the CBS
            // never reads it and never writes it.
            if (await client.ExistsAsync(partialPath, token))
                await client.DeleteFileAsync(partialPath, token);

            using var source = new MemoryStream(content, writable: false);
            await client.UploadFileAsync(source, partialPath, token);

            // Overwrite: a deposit retried after an ambiguous failure must converge, and the
            // sequence number already makes a second file for the same cycle impossible.
            await client.RenameFileAsync(partialPath, finalPath, token);

            logger.LogInformation(
                "Deposited {FileName} ({SizeBytes} bytes) in {Directory} for connection "
                + "{ConnectionId}",
                fileName, content.Length, settings.OutboundDirectory, connection.Id);

            return IntegrationResult.Ok(true);
        }, ct);

        return Collapse(result);
    }

    public async Task<IntegrationResult<IReadOnlyList<string>>> ListInboundAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var settings = BatchSettings(connection);
        if (settings is null)
            return Fail<IReadOnlyList<string>>(MissingBatchSettings(connection));

        if (string.IsNullOrWhiteSpace(settings.InboundDirectory))
            return IntegrationResult.Technical<IReadOnlyList<string>>(
                IntegrationErrors.SettingsInvalid,
                "No InboundDirectory is configured on this connection; there is nothing to poll.");

        return await WithClientAsync(connection, settings, async (client, token) =>
        {
            var names = new List<(string Name, DateTime Written)>();

            await foreach (var entry in client.ListDirectoryAsync(settings.InboundDirectory!, token))
            {
                if (entry.IsDirectory) continue;

                // Our own in-flight writes are not inbound files, and neither is the archive.
                if (entry.Name.EndsWith(PartialSuffix, StringComparison.Ordinal)) continue;

                names.Add((entry.Name, entry.LastWriteTimeUtc));
            }

            // "Oldest first where the server says so" — the seam's wording, and the ordering
            // matters: an acknowledgement file applies to a sequence, so reading them out of
            // order would close commands against the wrong batch.
            IReadOnlyList<string> ordered =
                [.. names.OrderBy(n => n.Written).ThenBy(n => n.Name, StringComparer.Ordinal)
                         .Select(n => n.Name)];

            return IntegrationResult.Ok(ordered);
        }, ct);
    }

    public async Task<IntegrationResult<byte[]>> GetInboundAsync(
        IntegrationConnection connection, string fileName, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var settings = BatchSettings(connection);
        if (settings is null) return Fail<byte[]>(MissingBatchSettings(connection));

        if (string.IsNullOrWhiteSpace(settings.InboundDirectory))
            return IntegrationResult.Technical<byte[]>(
                IntegrationErrors.SettingsInvalid, "No InboundDirectory is configured.");

        if (!IsSafeFileName(fileName))
            return IntegrationResult.Technical<byte[]>(
                IntegrationErrors.SettingsInvalid,
                "An inbound file name must be a single safe path segment.");

        return await WithClientAsync(connection, settings, async (client, token) =>
        {
            var path = Join(settings.InboundDirectory!, fileName);

            // The size is read BEFORE the bytes, which is what makes maxBytes a refusal rather
            // than a truncation: a half-read acknowledgement file would close the wrong commands.
            var attributes = await client.GetAttributesAsync(path, token);
            if (attributes.Size > maxBytes)
                return IntegrationResult.Functional<byte[]>(
                    IntegrationErrors.UnexpectedResponse,
                    $"Inbound file '{fileName}' is {attributes.Size} bytes, over the "
                    + $"{maxBytes}-byte ceiling; refusing to read it rather than truncating.");

            using var buffer = new MemoryStream();
            await client.DownloadFileAsync(path, buffer, token);

            return IntegrationResult.Ok(buffer.ToArray());
        }, ct);
    }

    public async Task<IntegrationResult> ArchiveInboundAsync(
        IntegrationConnection connection, string fileName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var settings = BatchSettings(connection);
        if (settings is null) return MissingBatchSettings(connection);

        if (string.IsNullOrWhiteSpace(settings.InboundDirectory))
            return IntegrationResult.Technical(
                IntegrationErrors.SettingsInvalid, "No InboundDirectory is configured.");

        if (!IsSafeFileName(fileName))
            return IntegrationResult.Technical(
                IntegrationErrors.SettingsInvalid,
                "An inbound file name must be a single safe path segment.");

        var result = await WithClientAsync(connection, settings, async (client, token) =>
        {
            var archive = Join(settings.InboundDirectory!, ArchiveFolder);

            if (!await client.ExistsAsync(archive, token))
                await client.CreateDirectoryAsync(archive, token);

            await client.RenameFileAsync(
                Join(settings.InboundDirectory!, fileName), Join(archive, fileName), token);

            return IntegrationResult.Ok(true);
        }, ct);

        return Collapse(result);
    }

    // ── Session handling ────────────────────────────────────────────────────

    /// <summary>
    /// Opens a verified session, runs one operation, and maps every way SSH.NET can fail onto the
    /// three families.
    ///
    /// <para>
    /// One place, so the host-key check cannot be present on the deposit and missing on the poll
    /// — which is the shape this kind of bug takes. The credential and the fingerprint are read
    /// per call rather than cached: both are rotated by an administrator while the process runs,
    /// and a cached host key is a host key that keeps admitting a server after its key was
    /// replaced because it was compromised.
    /// </para>
    /// </summary>
    private async Task<IntegrationResult<T>> WithClientAsync<T>(
        IntegrationConnection connection,
        BatchCapableSettings settings,
        Func<SftpClient, CancellationToken, Task<IntegrationResult<T>>> operation,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.SftpHost))
            return IntegrationResult.Technical<T>(
                IntegrationErrors.SettingsInvalid, "No SftpHost is configured on this connection.");

        if (string.IsNullOrWhiteSpace(settings.SftpUsername))
            return IntegrationResult.Technical<T>(
                IntegrationErrors.SettingsInvalid, "No SftpUsername is configured on this connection.");

        if (settings.SftpPort is < 1 or > 65535)
            return IntegrationResult.Technical<T>(
                IntegrationErrors.SettingsInvalid,
                $"SftpPort {settings.SftpPort} is not a TCP port.");

        // DIRECT PATH ONLY. A relay-routed connection is served by RelayFileTransport, which
        // opens no socket from this process; reaching here with one would mean the router was
        // bypassed, and the guard below would be asked to validate an address that is SUPPOSED to
        // be private. Refused rather than resolved, so a mis-wiring cannot become an egress.
        if (connection.RelayAgentId is not null || connection.Mode == IntegrationMode.Relay)
            return IntegrationResult.Technical<T>(
                IntegrationErrors.SettingsInvalid,
                "This connection is routed through a relay agent and must not be reached "
                + "directly; it is served by the relay transport. Resolve "
                + $"{nameof(IIntegrationFileTransport)} through "
                + $"{nameof(IntegrationFileTransportRouter)} rather than by concrete type.");

        // Resolved and validated BEFORE anything is opened AND before the vault is read, which
        // is deliberate on both counts: nothing about this connection leaves the process for a
        // target that is refused, and "no connection was attempted" becomes observable — the
        // credential was never even fetched. See SftpEgressGuard for why the validated ADDRESS,
        // not the name, is what the session is then built on.
        var target = await egress.ResolveAsync(settings.SftpHost!, settings.SftpPort, connection.Id, ct);

        if (target is null)
            return IntegrationResult.Transient<T>(IntegrationErrors.Unavailable, UnreachableDetail);

        var credential = await secrets.GetValueAsync(
            IntegrationSecrets.SftpCredentialKey(connection.TenantId, connection.Id), ct);

        if (string.IsNullOrWhiteSpace(credential))
            return IntegrationResult.Technical<T>(
                IntegrationErrors.CredentialMissing,
                $"No SFTP credential is stored for this connection (vault name "
                + $"'{IntegrationSecrets.SftpCredentialName}').");

        var fingerprint = await secrets.GetValueAsync(
            IntegrationSecrets.SftpHostKeyFingerprintKey(connection.TenantId, connection.Id), ct);

        // FAIL CLOSED. Not a warning, not a first-use acceptance: with no fingerprint there is
        // nothing to distinguish the IMF's server from anything that answers in its place, and
        // the bytes we are about to send are that IMF's customers.
        if (string.IsNullOrWhiteSpace(fingerprint))
            return IntegrationResult.Technical<T>(
                IntegrationErrors.SettingsInvalid,
                "No SFTP host key fingerprint is stored for this connection (vault name "
                + $"'{IntegrationSecrets.SftpHostKeyFingerprintName}'). Refusing to connect: an "
                + "unverified host key makes the deposit interceptable. Obtain it with "
                + "`ssh-keyscan -p <port> <host> | ssh-keygen -lf -` and store it as a connection "
                + "secret.");

        var timeout = ResolveTimeout(settings);

        AuthenticationMethod authentication;
        try
        {
            authentication = BuildAuthentication(settings.SftpUsername!, credential!);
        }
        catch (SshException ex)
        {
            // An unreadable or passphrase-protected private key. OUR configuration, so Technical:
            // retrying cannot make the key parse.
            logger.LogError(
                ex, "The stored SFTP credential of connection {ConnectionId} is not usable",
                connection.Id);

            return IntegrationResult.Technical<T>(
                IntegrationErrors.CredentialMissing,
                "The stored SFTP credential could not be read as a private key. A passphrase-"
                + "protected key is not supported; store an unencrypted key or a password.");
        }

        // target.HostArgument, never settings.SftpHost: connecting by name here would re-resolve
        // it and a DNS answer that changed in between (rebinding) would walk past the guard. The
        // host key check is unaffected — a server's key is the server's, not its name's.
        var connectionInfo = new ConnectionInfo(
            target.HostArgument, target.Port, settings.SftpUsername!, authentication)
        {
            Timeout = timeout,
        };

        using var client = new SftpClient(connectionInfo) { OperationTimeout = timeout };

        // Captured so the refusal can be reported as a refusal. Without it an untrusted key
        // surfaces as a generic "connection failed", which an operator reads as a network problem
        // and answers by retrying for an hour.
        var hostKeyRejected = false;

        client.HostKeyReceived += (_, e) =>
        {
            // Starts false and is only ever raised on a match. Written this way round on purpose:
            // the default of HostKeyEventArgs.CanTrust is true, so a handler that merely forgot a
            // branch would accept every key.
            e.CanTrust = SftpHostKeyFingerprint.Matches(e.HostKey, fingerprint);

            if (e.CanTrust) return;

            hostKeyRejected = true;

            // The presented fingerprint IS logged, the expected one is not: an operator needs to
            // see what answered in order to decide whether a key rotation or an interception
            // happened, and the vault value stays in the vault.
            logger.LogError(
                "SFTP host key refused for connection {ConnectionId} ({Host}:{Port}): the server "
                + "presented {Presented}, which does not match the stored fingerprint",
                connection.Id, settings.SftpHost, settings.SftpPort, e.FingerPrintSHA256);
        };

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout);

            await client.ConnectAsync(budget.Token);

            return await operation(client, budget.Token);
        }
        catch (SshConnectionException) when (hostKeyRejected)
        {
            // The actionable sentence — "the key was rotated, store the new fingerprint, or
            // something is answering in its place" — is already in the Error line the handler
            // logged. It must not come back to the caller: an answer that distinguishes "wrong
            // key" from "nothing there" is the oracle UnreachableDetail exists to close.
            return IntegrationResult.Transient<T>(IntegrationErrors.Unavailable, UnreachableDetail);
        }
        catch (SshAuthenticationException ex)
        {
            logger.LogError(ex, "SFTP authentication refused for connection {ConnectionId}", connection.Id);

            return IntegrationResult.Technical<T>(
                IntegrationErrors.AuthenticationRefused,
                "The SFTP server refused the stored credential.");
        }
        catch (SftpPermissionDeniedException ex)
        {
            logger.LogError(ex, "SFTP permission denied for connection {ConnectionId}", connection.Id);

            return IntegrationResult.Technical<T>(
                IntegrationErrors.AuthenticationRefused,
                "The SFTP account is not allowed to perform this operation in the configured "
                + "directory.");
        }
        catch (SftpPathNotFoundException ex)
        {
            // A missing directory is our configuration, not an outage: retrying for hours would
            // never create it, and the administrator who typed the path is the fix.
            logger.LogError(ex, "SFTP path not found for connection {ConnectionId}", connection.Id);

            return IntegrationResult.Technical<T>(
                IntegrationErrors.SettingsInvalid,
                "A configured SFTP directory or file does not exist on the server.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogError(
                "SFTP operation for connection {ConnectionId} did not complete within "
                + "{TimeoutSeconds:0} seconds ({Host}:{Port})",
                connection.Id, timeout.TotalSeconds, settings.SftpHost, settings.SftpPort);

            // Same sentence and same family as an unreachable host: a timeout that could be told
            // apart from a refusal is a port scanner.
            return IntegrationResult.Transient<T>(IntegrationErrors.Unavailable, UnreachableDetail);
        }
        catch (Exception ex) when (ex is SshOperationTimeoutException or SocketException
                                      or SshConnectionException or ProxyException or SftpException
                                      or SshException or IOException)
        {
            // Everything that remains is the far end or the network. Transient, so the batch job
            // comes back — the file stays Generated and nothing claims to have been deposited.
            // The host and port ARE logged and are deliberately absent from the result: echoing
            // the target back would confirm, address by address, which of them answer.
            logger.LogError(
                ex, "SFTP transport failed for connection {ConnectionId} ({Host}:{Port} at {Address})",
                connection.Id, settings.SftpHost, settings.SftpPort, target.Address);

            return IntegrationResult.Transient<T>(IntegrationErrors.Unavailable, UnreachableDetail);
        }
    }

    /// <summary>
    /// A PEM private key, or a password. Sniffed on the PEM header rather than configured,
    /// because <c>BatchCapableSettings</c> has no field saying which and adding one would publish
    /// through the API which kind of credential an institution uses.
    /// </summary>
    private static AuthenticationMethod BuildAuthentication(string username, string credential)
    {
        if (!credential.TrimStart().StartsWith("-----BEGIN", StringComparison.Ordinal))
            return new PasswordAuthenticationMethod(username, credential);

        using var key = new MemoryStream(Encoding.ASCII.GetBytes(credential));
        return new PrivateKeyAuthenticationMethod(username, new PrivateKeyFile(key));
    }

    private static TimeSpan ResolveTimeout(BatchCapableSettings settings)
    {
        if (settings.TimeoutSeconds <= 0) return DefaultTimeout;

        var configured = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        return configured < MinTimeout ? MinTimeout : configured > MaxTimeout ? MaxTimeout : configured;
    }

    private static BatchCapableSettings? BatchSettings(IntegrationConnection connection)
        => connection.Settings as BatchCapableSettings;

    private static IntegrationResult MissingBatchSettings(IntegrationConnection connection)
        => IntegrationResult.Technical(
            IntegrationErrors.SettingsInvalid,
            $"Connection {connection.Id} is of kind {connection.Kind}, whose settings carry no "
            + "file-exchange coordinates; only a batch-capable kind can be reached over SFTP.");

    private static IntegrationResult<T> Fail<T>(IntegrationResult failure)
        => failure.Family switch
        {
            ErrorFamily.Functional => IntegrationResult.Functional<T>(failure.Code!, failure.Detail),
            ErrorFamily.Transient => IntegrationResult.Transient<T>(failure.Code!, failure.Detail),
            _ => IntegrationResult.Technical<T>(failure.Code!, failure.Detail),
        };

    /// <summary>
    /// Drops the value of a generic result while KEEPING ITS FAMILY. The family is what decides
    /// whether the batch job ever comes back, so collapsing every deposit failure to Transient
    /// would retry a missing directory for ever and collapsing it to Technical would park a
    /// five-minute outage in the rejection queue.
    /// </summary>
    private static IntegrationResult Collapse<T>(IntegrationResult<T> result)
        => result.IsSuccess
            ? IntegrationResult.Ok()
            : result.Family switch
            {
                ErrorFamily.Functional => IntegrationResult.Functional(result.Code!, result.Detail),
                ErrorFamily.Transient => IntegrationResult.Transient(result.Code!, result.Detail),
                _ => IntegrationResult.Technical(result.Code!, result.Detail),
            };

    /// <summary>
    /// One path segment, no separators, no traversal. The outbound name is generated by this
    /// module and the inbound one comes from a directory listing, so neither is attacker-chosen
    /// today — this is the guard that keeps it true the day a name arrives from an endpoint.
    /// </summary>
    private static bool IsSafeFileName(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName)
           && fileName.Length <= 255
           && fileName is not ("." or "..")
           && !fileName.Any(c => c is '/' or '\\' or '\0' || char.IsControl(c));

    /// <summary>
    /// Remote paths are POSIX. <c>Path.Combine</c> would use the HOST's separator, so a Windows
    /// build would deposit into <c>out\file.csv</c> — one literal file with a backslash in its
    /// name, which the CBS never finds.
    /// </summary>
    private static string Join(string directory, string name)
        => $"{directory.TrimEnd('/')}/{name}";
}

