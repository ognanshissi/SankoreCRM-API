namespace Sankore.Modules.Leads.Domain;

/// <summary>Channel / medium through which the prospect's consent was obtained.</summary>
public enum ConsentChannel
{
    WebForm,
    Email,
    Sms,
    Phone,
    InPerson,
    Paper,
    Import,
    Other,

    /// <summary>
    /// Given on WhatsApp itself — the customer wrote to the institution's number (WA-BE-05, « message
    /// entrant »). Appended.
    /// </summary>
    WhatsApp
}
