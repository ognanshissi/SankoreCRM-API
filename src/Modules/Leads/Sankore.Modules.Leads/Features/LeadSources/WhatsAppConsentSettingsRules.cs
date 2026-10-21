namespace Sankore.Modules.Leads.Features.LeadSources;

using Newtonsoft.Json.Linq;

/// <summary>
/// The checks of the two WhatsApp opt-in settings of a source (WA-BE-05), shared by the create and
/// update validators so the two cannot disagree.
/// </summary>
internal static class WhatsAppConsentSettingsRules
{
    public const int MaxTextLength = 500;

    /// <summary>
    /// A path the ingestion can evaluate. Checked against an empty object, which is enough to
    /// refuse a syntax error: a path that fails at every lead would record no consent, silently.
    /// </summary>
    public static bool IsReadablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (path.Length > 200) return false;

        try
        {
            _ = new JObject().SelectToken(path.Trim());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
