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
    Other
}
