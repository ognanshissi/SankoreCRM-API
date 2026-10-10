namespace Sankore.Integration.RelayAgent.Execution;

using System.Text.Json;
using System.Text.RegularExpressions;
using Sankore.Integration.RelayAgent.Protocol;

/// <summary>
/// Decoding and bounding what arrives on the wire. Everything an executor reads from an order
/// passes through here first.
/// </summary>
internal static partial class OrderBody
{
    /// <summary>
    /// A bare file name. No directory separator in either flavour, no drive letter, no dots on
    /// their own.
    ///
    /// <para>
    /// A whitelist and not a check for <c>".."</c>, because the blacklist is the version that
    /// keeps losing: <c>..%2f</c>, <c>....//</c>, a backslash on a server that happens to accept
    /// one, a NUL truncating the rest. If the only characters admitted are letters, digits and
    /// three punctuation marks, there is no encoding of a path that survives.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFileName();

    /// <summary>
    /// Decodes an order's body, returning false rather than throwing. A malformed frame is a
    /// refusal we answer, not an exception that kills the session for every other order in
    /// flight.
    /// </summary>
    public static bool TryRead<T>(RelayOrder order, out T? body)
    {
        try
        {
            body = order.Body.Deserialize<T>(RelayProtocolJson.Options);
            return body is not null;
        }
        catch (JsonException)
        {
            body = default;
            return false;
        }
    }

    /// <summary>True when the name is a bare file name this agent will act on.</summary>
    public static bool IsSafeFileName(string? name)
        => !string.IsNullOrEmpty(name)
           && SafeFileName().IsMatch(name)
           && name is not ("." or "..");

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> from <paramref name="source"/> into memory, and
    /// reports whether it stopped early.
    ///
    /// <para>
    /// Memory, never a temporary file — criterion 4's "keeps no data beyond the processing in
    /// flight" is enforced by there being no code in this project that opens a file for writing.
    /// The cap is what makes that safe: a relayed system answering an unexpected gigabyte must
    /// fail the order, not the process.
    /// </para>
    /// </summary>
    public static async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(
        Stream source, int maxBytes, CancellationToken cancellationToken)
    {
        // One byte over the cap, so hitting it is distinguishable from fitting exactly.
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        var total = 0;

        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            total += read;
            if (total > maxBytes) return ([], true);

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), false);
    }
}
