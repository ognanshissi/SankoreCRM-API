namespace Sankore.Modules.Leads.Features.WhatsApp;

using System.Text;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// The error codes of the WhatsApp slices. One list, so the front end's French messages and the
/// handlers cannot spell a code two ways.
/// </summary>
public static class WhatsAppErrors
{
    public const string LeadNotFound = "LEAD_NOT_FOUND";

    /// <summary>The lead is converted, lost, disqualified or archived — nobody works it any more.</summary>
    public const string LeadClosed = "WHATSAPP_LEAD_CLOSED";

    /// <summary>No WhatsApp consent was ever recorded (« non renseigné »).</summary>
    public const string ConsentMissing = "WHATSAPP_CONSENT_MISSING";

    /// <summary>The customer withdrew it (« révoqué »).</summary>
    public const string ConsentRevoked = "WHATSAPP_CONSENT_REVOKED";

    /// <summary>The lead's number cannot be written in E.164 — no indicatif, too short, too long.</summary>
    public const string PhoneInvalid = "WHATSAPP_PHONE_INVALID";

    /// <summary>The tenant has no active number for this lead's agency.</summary>
    public const string ChannelInactive = "WHATSAPP_CHANNEL_INACTIVE";

    public const string TemplateNotFound = "WHATSAPP_TEMPLATE_NOT_FOUND";

    /// <summary>Not approved by Meta, or no longer listed. Never sent.</summary>
    public const string TemplateNotApproved = "WHATSAPP_TEMPLATE_NOT_APPROVED";

    /// <summary>The template is configured for other products than the lead's.</summary>
    public const string TemplateNotForProduct = "WHATSAPP_TEMPLATE_NOT_FOR_PRODUCT";

    /// <summary>Not one value per variable, or an empty or oversized one.</summary>
    public const string VariablesInvalid = "WHATSAPP_VARIABLES_INVALID";

    /// <summary>A template went out less than 24 hours ago and the customer has not answered.</summary>
    public const string TemplateCooldown = "WHATSAPP_TEMPLATE_COOLDOWN";

    /// <summary>The person asked to be forgotten (data-purge lot 3): we no longer start a contact.</summary>
    public const string ProcessingRestricted = Retention.ProcessingRestriction.Error;

    public const string MessageNotFound = "WHATSAPP_MESSAGE_NOT_FOUND";

    /// <summary>The message carries no media to download.</summary>
    public const string NoMedia = "WHATSAPP_NO_MEDIA";

    /// <summary>The media reached its retention and was deleted (WA-BE-21).</summary>
    public const string MediaPurged = "WHATSAPP_MEDIA_PURGED";

    /// <summary>The conversation is already filed on a lead.</summary>
    public const string AlreadyAttached = "WHATSAPP_CONVERSATION_ALREADY_ATTACHED";

    /// <summary>A WhatsApp consent typed by an agent must say why (WA-BE-05).</summary>
    public const string ConsentReasonRequired = "WHATSAPP_CONSENT_REASON_REQUIRED";

    /// <summary>After a withdrawal, a new consent needs a new proof — a logged call or visit.</summary>
    public const string ConsentNewProofRequired = "WHATSAPP_CONSENT_NEW_PROOF_REQUIRED";

    /// <summary>The 360dialog send failed on our side before reaching the provider.</summary>
    public const string SendFailed = "WHATSAPP_SEND_FAILED";

    /// <summary>The consumer found the message already being sent — its fate is unknown.</summary>
    public const string SendUncertain = "WHATSAPP_SEND_UNCERTAIN";
}

/// <summary>Telephone numbers in the form Meta wants.</summary>
public static class WhatsAppNumbers
{
    /// <summary>
    /// The number in E.164 (<c>+</c> and 8 to 15 digits), or null when it cannot be written so.
    ///
    /// <para>
    /// <b>A local number takes the tenant's calling code</b> — <c>DefaultCallingCode</c> of the SMS
    /// settings, the one place this platform records the country — exactly as the SMS channel does
    /// (<c>ClientSmsNotifier.ToE164</c>), and with the same accepted limitation: a trunk zero is NOT
    /// dropped, because the institutions are UEMOA ones whose numbering plans have none, and
    /// dropping a significant zero would write to a stranger. Without a calling code, a local
    /// number is refused rather than guessed.
    /// </para>
    /// </summary>
    public static string? ToE164(string? raw, string? defaultCallingCode)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();
        var international = value.StartsWith('+') || value.StartsWith("00", StringComparison.Ordinal);

        var digits = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c)) digits.Append(c);
            else if (c is not (' ' or '-' or '.' or '(' or ')' or '+' or '/')) return null;
        }

        var number = digits.ToString();

        if (value.StartsWith("00", StringComparison.Ordinal)) number = number[2..];

        if (!international)
        {
            var code = new string((defaultCallingCode ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
            if (code.Length == 0) return null;

            number = code + number;
        }

        return number.Length is >= 8 and <= 15 ? "+" + number : null;
    }

    /// <summary>The last four digits, for a screen or a log that must not show the number.</summary>
    public static string Mask(string? e164)
        => string.IsNullOrEmpty(e164) || e164.Length < 4 ? "••••" : "••" + e164[^4..];
}

/// <summary>
/// Where a lead stands on WhatsApp consent, read from its consent rows (WA-BE-05, WA-FE-03).
/// </summary>
public enum WhatsAppConsentState
{
    /// <summary>No row — « Non renseigné ».</summary>
    Unknown,

    /// <summary>An active row — « Consenti ».</summary>
    Granted,

    /// <summary>The newest row is a withdrawal or a refusal — « Révoqué ».</summary>
    Revoked,
}

/// <summary>
/// Why the « Contacter via WhatsApp » button is disabled, one code per obstacle (WA-FE-04).
///
/// <para>
/// Computed once, here, and read by both the screen and the send: a button that offered a send
/// the command then refused would be the screen and the server disagreeing about one rule, which
/// is the defect this shape exists to prevent.
/// </para>
/// </summary>
public static class WhatsAppEligibility
{
    public static IReadOnlyList<string> Blockers(
        Lead lead,
        WhatsAppConsentState consent,
        string? phoneE164,
        bool channelActive,
        WhatsAppConversation? conversation,
        bool mayForce,
        DateTimeOffset now,
        bool processingRestricted = false)
    {
        var blockers = new List<string>();

        // First: neither consent nor a forced send overrides a person's request to be forgotten.
        if (processingRestricted) blockers.Add(WhatsAppErrors.ProcessingRestricted);

        if (!lead.IsDispatchable) blockers.Add(WhatsAppErrors.LeadClosed);

        if (consent == WhatsAppConsentState.Unknown) blockers.Add(WhatsAppErrors.ConsentMissing);
        else if (consent == WhatsAppConsentState.Revoked) blockers.Add(WhatsAppErrors.ConsentRevoked);

        if (phoneE164 is null) blockers.Add(WhatsAppErrors.PhoneInvalid);
        if (!channelActive) blockers.Add(WhatsAppErrors.ChannelInactive);

        if (!mayForce && conversation?.NextTemplateAllowedAt(now) is not null)
            blockers.Add(WhatsAppErrors.TemplateCooldown);

        return blockers;
    }

    /// <summary>The consent state from a lead's WhatsApp consent rows, newest decision first.</summary>
    public static WhatsAppConsentState ConsentStateOf(IEnumerable<LeadConsent> consents)
    {
        var rows = consents
            .Where(c => c.Type == ConsentType.WhatsAppContact && c.SubjectId == null)
            .ToList();

        if (rows.Count == 0) return WhatsAppConsentState.Unknown;
        if (rows.Any(c => c.Status == ConsentStatus.Active)) return WhatsAppConsentState.Granted;

        return WhatsAppConsentState.Revoked;
    }
}
