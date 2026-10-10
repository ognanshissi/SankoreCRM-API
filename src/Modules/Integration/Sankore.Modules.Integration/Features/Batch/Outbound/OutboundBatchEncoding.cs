namespace Sankore.Modules.Integration.Features.Batch.Outbound;

using System.Text;

/// <summary>
/// Resolves <c>BatchCapableSettings.FileEncoding</c> — the "configurable encoding" of INT-24
/// criterion 2.
///
/// <para>
/// <b>An unknown name fails, loudly, and is never silently replaced by UTF-8.</b> A West-African
/// CBS is rarely UTF-8, and a file written in the wrong encoding is not rejected by a machine: it
/// is read as mojibake and refused hours later by a human at the IMF, who reports it as "the names
/// are wrong" rather than as an encoding fault. A failure at generation names the setting and the
/// connection; a silent fallback names nothing.
/// </para>
///
/// <para>
/// A BLANK name is not an unknown name — it is an unconfigured one, and the settings record's own
/// default is UTF-8, so blank resolves to UTF-8 without a BOM. Without a BOM deliberately: a
/// fixed-width or delimited parser on the CBS side reads the three preamble bytes as the first
/// field of the first record, which corrupts exactly one row per file and is the single hardest
/// batch fault to diagnose.
/// </para>
///
/// <para>
/// Only the encodings the runtime itself carries resolve. .NET Core ships Unicode, ASCII and
/// Latin-1; a single-byte code page such as <c>windows-1252</c> needs
/// <c>System.Text.Encoding.CodePages</c>, which this module does NOT reference — so such a name
/// fails here with a message saying so, rather than appearing to work.
/// </para>
/// </summary>
internal static class OutboundBatchEncoding
{
    /// <summary>
    /// The encoding, or <c>null</c> with <paramref name="error"/> set to an operator-facing
    /// sentence. A <see cref="bool"/>-returning shape rather than an exception because the caller
    /// turns this into a <c>Technical</c> <c>IntegrationResult</c>, and a batch job must record a
    /// misconfiguration rather than fault with a stack trace nobody maps back to a tenant.
    /// </summary>
    internal static bool TryResolve(string? name, out Encoding encoding, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            encoding = NoBomUtf8;
            return true;
        }

        var requested = name.Trim();

        try
        {
            var resolved = Encoding.GetEncoding(requested);

            // UTF-8 is handed back with a preamble-emitting instance; swapped for the BOM-less one
            // so the choice above is honoured however the name was spelled ("utf-8", "UTF8", 65001).
            encoding = resolved.CodePage == Encoding.UTF8.CodePage ? NoBomUtf8 : resolved;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            encoding = NoBomUtf8;
            error =
                $"FileEncoding '{requested}' is not an encoding this runtime can provide. "
                + "Available without an extra package: utf-8, utf-16, utf-32, us-ascii, "
                + "iso-8859-1 (latin1). A single-byte code page such as windows-1252 requires "
                + "System.Text.Encoding.CodePages, which this deployment does not carry. "
                + "Refusing to generate the file rather than writing it in the wrong encoding.";

            return false;
        }
    }

    /// <summary>UTF-8 that emits no byte-order mark and throws on unmappable input.</summary>
    private static readonly Encoding NoBomUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
