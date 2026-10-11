namespace Sankore.Modules.Leads.Features.Settings;

/// <summary>
/// Error codes of this area. <see cref="Unknown"/> repeats the literal
/// <c>LeadSettingsService</c> already answers, rather than introducing a second spelling for the
/// same refusal: settings are a closed list, and a caller cannot tell which of the two layers
/// rejected the key.
/// </summary>
internal static class LeadSettingErrors
{
    public const string Unknown = "SETTING_UNKNOWN";

    /// <summary>
    /// The retention preview was asked for while the period is zero or negative. The run SKIPS in
    /// that case rather than destroying a lead the moment it closes, so the preview says so
    /// instead of answering a count of zero — which would read as "nothing to destroy".
    /// </summary>
    public const string RetentionNotConfigured = "LEAD_RETENTION_NOT_CONFIGURED";
}
