namespace Sankore.Modules.Integration.Infrastructure.ClaimStorage;

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Settings of the ClamAV scanner, bound from <c>Integration:Insurance:Antivirus</c>.</summary>
internal sealed class ClamAvOptions
{
    public const string SectionName = "Integration:Insurance:Antivirus";

    /// <summary>
    /// <c>host:port</c> of a clamd listening for TCP. EMPTY by default, and that is what selects
    /// <see cref="UnconfiguredClaimDocumentScanner"/> — a deployment opts IN to scanning, and
    /// until it does, nothing is transmittable.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>Seconds to wait for a verdict. A large PDF on a busy daemon takes seconds, not minutes.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    internal int ResolvedTimeoutSeconds => TimeoutSeconds is > 0 and <= 300 ? TimeoutSeconds : 30;
}

/// <summary>
/// Scans a claim piece through clamd's <c>INSTREAM</c> command over TCP.
///
/// <para>
/// <b>INSTREAM and not SCAN.</b> <c>SCAN &lt;path&gt;</c> would need the daemon to see the same
/// filesystem as this process and would need the PLAINTEXT written to a disk for it to read — a
/// temporary file holding an unscanned claim piece in clear, which is the one artefact the
/// encrypted store exists to avoid. <c>INSTREAM</c> pushes the bytes down the socket and they
/// never touch a disk outside the store.
/// </para>
///
/// <para>
/// <b>What has and has not been exercised.</b> The framing and the response parsing are pure
/// functions with cases of their own below; the socket plumbing around them has NOT been run
/// against a real clamd in this repository, and that is stated here rather than implied by the
/// absence of a note. A deployment turning this on should verify it once with EICAR, which is
/// exactly what <see cref="ParseResponse"/>'s own cases are written around.
/// </para>
/// </summary>
internal sealed class ClamAvClaimDocumentScanner(
    IOptions<ClamAvOptions> options,
    ILogger<ClamAvClaimDocumentScanner> logger)
    : IClaimDocumentScanner
{
    /// <summary>
    /// clamd's own INSTREAM chunk ceiling is <c>StreamMaxLength</c>, 25 MB by default. 64 KiB
    /// chunks sit far below any configuration of it and match the daemon's own read buffer.
    /// </summary>
    private const int ChunkSize = 64 * 1024;

    public async Task<ClaimScanVerdict> ScanAsync(byte[] plaintext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var address = options.Value.Address;

        if (!TryParseAddress(address, out var host, out var port))
        {
            // Not an exception: the job records a verdict per piece, and a misconfigured address
            // must read as "the scanner told us nothing" rather than kill the sweep.
            return ClaimScanVerdict.Failed(
                $"Antivirus address '{address}' is not host:port.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ResolvedTimeoutSeconds));

        try
        {
            using var client = new TcpClient();

            await client.ConnectAsync(host, port, timeout.Token);

            await using var stream = client.GetStream();

            // "zINSTREAM\0" — the z prefix asks clamd to NUL-terminate its answer, which is what
            // makes the read below terminate on a boundary instead of on the connection closing.
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);

            var header = new byte[4];

            for (var offset = 0; offset < plaintext.Length; offset += ChunkSize)
            {
                var length = Math.Min(ChunkSize, plaintext.Length - offset);

                // BIG-endian, which is what the protocol says and what a little-endian host gets
                // wrong silently: clamd would read a 64 KiB chunk as 1 GiB and abort the stream.
                BinaryPrimitives.WriteInt32BigEndian(header, length);

                await stream.WriteAsync(header, timeout.Token);
                await stream.WriteAsync(plaintext.AsMemory(offset, length), timeout.Token);
            }

            // A zero-length chunk terminates the stream and asks for the verdict.
            BinaryPrimitives.WriteInt32BigEndian(header, 0);
            await stream.WriteAsync(header, timeout.Token);
            await stream.FlushAsync(timeout.Token);

            var answer = await ReadAnswerAsync(stream, timeout.Token);

            var verdict = ParseResponse(answer);

            if (verdict.Status == Domain.DocumentScanStatus.Infected)
            {
                logger.LogWarning(
                    "A claim piece was refused by the antivirus: {Signature}", verdict.Detail);
            }

            return verdict;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ClaimScanVerdict.Failed(
                $"The antivirus did not answer within {options.Value.ResolvedTimeoutSeconds}s.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            // Our outage, not the customer's file. Recorded as ScannerFailed so the piece stays
            // untransmittable and the next sweep tries again.
            logger.LogWarning(ex, "Could not reach the antivirus at {Address}", address);

            return ClaimScanVerdict.Failed($"Could not reach the antivirus: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Reads until clamd's NUL terminator, bounded. The cap is generous for a verdict line and
    /// exists so a daemon answering nonsense cannot make this read for ever.
    /// </summary>
    private static async Task<string> ReadAnswerAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[512];
        var builder = new StringBuilder();

        while (builder.Length < 1024)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;

            var span = buffer.AsSpan(0, read);
            var terminator = span.IndexOf((byte)0);

            if (terminator >= 0)
            {
                builder.Append(Encoding.ASCII.GetString(span[..terminator]));
                break;
            }

            builder.Append(Encoding.ASCII.GetString(span));
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// clamd's verdict line into a status. A pure function, and the one part of this class the
    /// suite exercises:
    /// <list type="bullet">
    /// <item><c>stream: OK</c> — clean;</item>
    /// <item><c>stream: Eicar-Test-Signature FOUND</c> — infected, and the signature is the
    ///   detail an operator is shown;</item>
    /// <item><c>INSTREAM size limit exceeded. ERROR</c> and anything else — OUR problem, not the
    ///   file's.</item>
    /// </list>
    ///
    /// <para>
    /// The default branch is <see cref="ClaimScanVerdict.Failed"/> and never
    /// <see cref="ClaimScanVerdict.Clean"/>: an answer this code does not understand must not pass
    /// a document to an insurer. That is the same posture <c>RequiredFieldEvaluator</c>'s
    /// <c>_ => false</c> takes — an unknown verdict blocks.
    /// </para>
    /// </summary>
    internal static ClaimScanVerdict ParseResponse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return ClaimScanVerdict.Failed("The antivirus answered nothing.");

        var line = answer.Trim();

        if (line.EndsWith("FOUND", StringComparison.Ordinal))
        {
            // "stream: <signature> FOUND" — take what is between the colon and FOUND, and fall
            // back to the whole line rather than losing the only thing an operator can act on.
            var colon = line.IndexOf(':', StringComparison.Ordinal);

            var signature = colon >= 0 && colon + 1 < line.Length
                ? line[(colon + 1)..^"FOUND".Length].Trim()
                : line;

            return ClaimScanVerdict.Infected(
                string.IsNullOrWhiteSpace(signature) ? line : signature);
        }

        // Checked AFTER the FOUND branch: clamd's clean answer ends in OK, and a signature named
        // something ending in "OK" would otherwise be read as clean.
        if (line.EndsWith("OK", StringComparison.Ordinal))
            return ClaimScanVerdict.Clean();

        return ClaimScanVerdict.Failed(
            line.Length <= 200 ? line : line[..200]);
    }

    /// <summary>
    /// <c>host:port</c>, with the port required. No default of 3310: a deployment that typed only
    /// a host has not finished configuring, and guessing the port would make the failure a
    /// connection timeout somewhere else.
    /// </summary>
    internal static bool TryParseAddress(string? address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(address)) return false;

        var separator = address.LastIndexOf(':');
        if (separator <= 0 || separator == address.Length - 1) return false;

        host = address[..separator].Trim();

        return host.Length > 0
            && int.TryParse(
                address[(separator + 1)..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out port)
            && port is > 0 and <= 65535;
    }
}
