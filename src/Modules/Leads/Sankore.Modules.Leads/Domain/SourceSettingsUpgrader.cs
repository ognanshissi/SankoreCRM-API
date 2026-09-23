namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Upgrades older schema versions of <see cref="SourceSettings"/> to the latest.
/// Called transparently on load via the EF ValueConverter.
/// The upgraded settings are written back on the next SaveChangesAsync.
/// </summary>
public static class SourceSettingsUpgrader
{
    /// <summary>Current schema version for all settings types.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Upgrades a deserialized settings instance to the current schema version.
    /// Returns the same instance if already current, or a new instance with defaults applied.
    /// </summary>
    public static SourceSettings Upgrade(SourceSettings settings)
    {
        if (settings.SchemaVersion >= CurrentVersion)
            return settings;

        // Future: add per-version upgrade logic here.
        // Example:
        // if (settings.SchemaVersion < 2 && settings is ServerWebhookSettings wh)
        //     return wh with { ContentType = wh.ContentType ?? "application/json", SchemaVersion = 2 };

        return settings with { SchemaVersion = CurrentVersion };
    }
}
