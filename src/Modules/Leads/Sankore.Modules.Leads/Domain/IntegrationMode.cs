namespace Sankore.Modules.Leads.Domain;

public enum IntegrationMode
{
    EmbeddedScript,
    ServerWebhook,
    ScheduledPull,
    PlatformConnection,
    SocialTracking,
    Internal
}
