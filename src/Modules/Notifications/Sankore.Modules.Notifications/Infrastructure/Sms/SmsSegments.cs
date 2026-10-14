namespace Sankore.Modules.Notifications.Infrastructure.Sms;

/// <summary>
/// How many segments a message will be billed as.
///
/// <para>
/// <b>Why this is worth a class rather than <c>body.Length / 160</c>.</b> An SMS is encoded in
/// GSM-7 when every character is in that alphabet and in UCS-2 otherwise, and the two have
/// different budgets: 160 characters against 70. A single accented character — "é" in "Bonjour
/// Aminata, votre dossier…" — pushes the whole message into UCS-2 and MORE THAN HALVES its
/// capacity. Counting characters against 160 would report one segment for a message the provider
/// bills as three, which is exactly the kind of error that shows up on an invoice rather than in a
/// log.
/// </para>
///
/// <para>
/// The seeded French bodies are written without accents for that reason, which is also why a
/// tenant editing one needs to see the segment count the write slice shows them.
/// </para>
/// </summary>
internal static class SmsSegments
{
    /// <summary>A single GSM-7 message.</summary>
    private const int Gsm7Single = 160;

    /// <summary>
    /// A concatenated GSM-7 message: 7 characters of each segment go to the UDH that tells the
    /// handset how to reassemble them.
    /// </summary>
    private const int Gsm7Concatenated = 153;

    /// <summary>A single UCS-2 message.</summary>
    private const int Ucs2Single = 70;

    /// <summary>A concatenated UCS-2 message.</summary>
    private const int Ucs2Concatenated = 67;

    /// <summary>
    /// The GSM 03.38 basic alphabet, plus the extension characters that cost TWO septets each
    /// (<c>^ { } \ [ ~ ] | €</c>) — which is why the count below adds 2 for them rather than 1.
    /// </summary>
    private const string Gsm7Basic =
        "@£$¥èéùìòÇ\nØø\rÅå_ÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?"
        + "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà"
        + "ΔΦΓΛΩΠΨΣΘΞ";

    private const string Gsm7Extended = "^{}\\[~]|€";

    internal static int Count(string? body)
    {
        if (string.IsNullOrEmpty(body)) return 0;

        if (!TryCountGsm7(body, out var septets))
        {
            // UCS-2. Counted in UTF-16 code units and not in runes, because that is what the
            // encoding bills: an emoji outside the BMP is a surrogate pair and costs two.
            var units = body.Length;

            return units <= Ucs2Single
                ? 1
                : (int)Math.Ceiling(units / (double)Ucs2Concatenated);
        }

        return septets <= Gsm7Single
            ? 1
            : (int)Math.Ceiling(septets / (double)Gsm7Concatenated);
    }

    /// <summary>
    /// The septet cost of <paramref name="body"/>, or false when any character is outside GSM-7
    /// and the whole message therefore goes out as UCS-2. One character decides it for all of
    /// them — there is no mixed encoding.
    /// </summary>
    private static bool TryCountGsm7(string body, out int septets)
    {
        septets = 0;

        foreach (var c in body)
        {
            if (Gsm7Extended.Contains(c, StringComparison.Ordinal))
            {
                // An escape plus the character itself.
                septets += 2;
                continue;
            }

            if (!Gsm7Basic.Contains(c, StringComparison.Ordinal))
            {
                septets = 0;
                return false;
            }

            septets++;
        }

        return true;
    }
}
