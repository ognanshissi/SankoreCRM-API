namespace Sankore.Modules.Leads.Domain;

/// <summary>Convenience accessors over the mode-specific <see cref="SourceSettings"/> types.</summary>
public static class SourceSettingsExtensions
{
    /// <summary>
    /// Returns the field mapping rules for any settings type that carries them,
    /// or null for the modes that have no mapping (EmbeddedScript, SocialTracking).
    /// </summary>
    public static IReadOnlyList<FieldMappingRule>? FieldMappingsOf(this SourceSettings? settings)
        => settings switch
        {
            ServerWebhookSettings wh   => wh.FieldMappings,
            ScheduledPullSettings pull => pull.FieldMappings,
            PlatformSettings plat      => plat.FieldMappings,
            InternalSettings local     => local.FieldMappings,
            _                          => null
        };
}
