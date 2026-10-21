namespace Sankore.Modules.Integration.Features.Messaging;

using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Meta's Cloud API error codes, translated into this module's <c>MESSAGING_*</c> codes
/// (WA-BE-01 criterion 3, WA-BE-08).
///
/// <para>
/// <b>One table for both directions a refusal can take.</b> A send refused on the spot answers an
/// HTTP error carrying the code; a send accepted and then not delivered arrives minutes later as a
/// <c>failed</c> status carrying the same code. M13 decides what an agent reads from the
/// translated code alone, so both paths must translate identically — two tables would eventually
/// tell an agent two different things about one failure.
/// </para>
///
/// <para>
/// The list is the short one an agent's screen needs: invalid number, refused template, outside
/// the window, throttled, not delivered. A code absent from it answers <c>null</c> and the caller
/// falls back on what it knows otherwise (an HTTP status, or a generic failure).
/// </para>
/// </summary>
public static class CloudApiErrors
{
    /// <summary>The recipient cannot receive WhatsApp messages from this number.</summary>
    public static readonly HashSet<int> RecipientCodes =
    [
        131026, // message undeliverable — not a WhatsApp user, or an old client
        131021, // recipient is the sender
        131030, // recipient not in the allowed list (test numbers)
        131045, // incorrect certificate / unregistered number
    ];

    /// <summary>The template does not exist, is not approved in that language, or was filled wrong.</summary>
    public static readonly HashSet<int> TemplateCodes =
    [
        132000, // number of parameters does not match
        132001, // template does not exist (in that language)
        132005, // hydrated text too long
        132007, // format character policy violated
        132012, // parameter format mismatch
        132015, // template paused
        132016, // template disabled
        132068, // flow is blocked
        132069, // flow is throttled
    ];

    /// <summary>More than 24 hours since the customer last wrote.</summary>
    public const int ReEngagementCode = 131047;

    /// <summary>Throughput, pair rate and spam rate limits.</summary>
    public static readonly HashSet<int> QuotaCodes =
    [
        4,      // application-level throttling
        80007,  // WABA rate limit
        130429, // Cloud API throughput reached
        131048, // spam rate limit
        131056, // pair rate limit — too many to the same number
    ];

    /// <summary>Meta accepted the request and chose not to deliver it.</summary>
    public const int NotDeliveredCode = 131049;

    /// <summary>The access token or API key is refused, or lacks the permission.</summary>
    public static readonly HashSet<int> AuthenticationCodes = [0, 3, 10, 190, 200];

    /// <summary>Meta's own service failure.</summary>
    public static readonly HashSet<int> UnavailableCodes = [1, 2, 131000, 131016];

    /// <summary>
    /// The request itself was malformed — ours to fix, never a customer's problem.
    /// </summary>
    public static readonly HashSet<int> PayloadCodes = [100, 131008, 131009, 131051, 131052, 131053];

    /// <summary>The translated code and its family, or null for a code this table does not know.</summary>
    public static (string Code, ErrorFamily Family)? Translate(int code)
    {
        if (RecipientCodes.Contains(code)) return (IntegrationErrors.MessagingRecipientInvalid, ErrorFamily.Functional);
        if (TemplateCodes.Contains(code)) return (IntegrationErrors.MessagingTemplateRejected, ErrorFamily.Functional);
        if (code == ReEngagementCode) return (IntegrationErrors.MessagingOutsideWindow, ErrorFamily.Functional);
        if (QuotaCodes.Contains(code)) return (IntegrationErrors.MessagingQuotaExceeded, ErrorFamily.Transient);
        if (code == NotDeliveredCode) return (IntegrationErrors.MessagingNotDelivered, ErrorFamily.Functional);
        if (AuthenticationCodes.Contains(code)) return (IntegrationErrors.AuthenticationRefused, ErrorFamily.Technical);
        if (UnavailableCodes.Contains(code)) return (IntegrationErrors.Unavailable, ErrorFamily.Transient);
        if (PayloadCodes.Contains(code)) return (IntegrationErrors.PayloadInvalid, ErrorFamily.Technical);

        return null;
    }

    /// <summary>
    /// The translated code for a status's error, as text — what M13 stores on a failed message.
    /// An unknown or unreadable code answers <see cref="IntegrationErrors.Rejected"/>: the message
    /// did fail, and saying so generically beats saying nothing.
    /// </summary>
    public static string TranslateStatusError(string? rawCode)
        => int.TryParse(rawCode, out var code) && Translate(code) is { } known
            ? known.Code
            : IntegrationErrors.Rejected;
}
