namespace Sankore.Modules.Leads.Domain;

/// <summary>Convenience accessors over the mode-specific <see cref="SourceSettings"/> types.</summary>
public static class SourceSettingsExtensions
{
    /// <summary>
    /// Returns the field mapping rules, or null when the source has none.
    ///
    /// <para>
    /// A one-line forward since <see cref="SourceSettings.FieldMappings"/> moved to the base,
    /// and kept rather than inlined at its twelve call sites: the switch it replaced listed four
    /// modes and answered null for EmbeddedScript and SocialTracking, so a mapping configured on
    /// a web-form source was silently ignored on both the write and the read side.
    /// </para>
    /// </summary>
    public static IReadOnlyList<FieldMappingRule>? FieldMappingsOf(this SourceSettings? settings)
        => settings?.FieldMappings;
}
