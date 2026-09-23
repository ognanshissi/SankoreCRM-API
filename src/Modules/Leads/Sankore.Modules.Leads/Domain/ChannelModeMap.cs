namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Maps each <see cref="LeadChannelType"/> to its allowed <see cref="IntegrationMode"/>s.
/// The first mode in each list is the default when the caller doesn't specify one.
/// </summary>
public static class ChannelModeMap
{
    private static readonly Dictionary<LeadChannelType, IReadOnlyList<IntegrationMode>> Map = new()
    {
        [LeadChannelType.WebForm]          = [IntegrationMode.EmbeddedScript, IntegrationMode.ServerWebhook],
        [LeadChannelType.InboundWebhook]   = [IntegrationMode.ServerWebhook],
        [LeadChannelType.ExternalApiPull]   = [IntegrationMode.ScheduledPull],
        [LeadChannelType.FacebookLeadAds]  = [IntegrationMode.PlatformConnection],
        [LeadChannelType.InstagramLeadAds] = [IntegrationMode.PlatformConnection],
        [LeadChannelType.LinkedInLeadGen]  = [IntegrationMode.PlatformConnection],
        [LeadChannelType.WhatsAppInbound]  = [IntegrationMode.PlatformConnection],
        [LeadChannelType.SocialEngagement] = [IntegrationMode.SocialTracking],
        [LeadChannelType.MobileAgent]      = [IntegrationMode.Internal],
        [LeadChannelType.WalkIn]           = [IntegrationMode.Internal],
        [LeadChannelType.SmsUssdCampaign]  = [IntegrationMode.ServerWebhook, IntegrationMode.Internal],
        [LeadChannelType.Referral]         = [IntegrationMode.Internal],
        [LeadChannelType.FileImport]       = [IntegrationMode.Internal],
        [LeadChannelType.InboundCall]      = [IntegrationMode.Internal, IntegrationMode.ServerWebhook],
    };

    /// <summary>Returns the allowed modes for a channel, or empty if the channel is unknown.</summary>
    public static IReadOnlyList<IntegrationMode> GetAllowedModes(LeadChannelType channelType)
        => Map.TryGetValue(channelType, out var modes) ? modes : [];

    /// <summary>Returns the default mode (first in the allowed list) for a channel.</summary>
    public static IntegrationMode? GetDefaultMode(LeadChannelType channelType)
        => Map.TryGetValue(channelType, out var modes) && modes.Count > 0 ? modes[0] : null;

    /// <summary>Checks whether a given mode is allowed for a channel.</summary>
    public static bool IsModeAllowed(LeadChannelType channelType, IntegrationMode integrationMode)
        => Map.TryGetValue(channelType, out var modes) && modes.Contains(integrationMode);

    /// <summary>Returns the full map for the metadata endpoint.</summary>
    public static IReadOnlyDictionary<LeadChannelType, IReadOnlyList<IntegrationMode>> GetAll() => Map;
}
