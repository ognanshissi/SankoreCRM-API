namespace Sankore.Modules.Integration.Features.Sync;

/// <summary>
/// The webhook's one switch (INT-20, criterion 4): how far from now a sender's timestamp may be.
///
/// <para>
/// A host setting and not a connection setting, deliberately. <c>ConnectionSettings</c> is per
/// tenant and editable through the API; the replay window is a platform security parameter, and a
/// tenant able to widen its own to a year would disable its anti-replay guarantee without anybody
/// noticing. The acceptable clock skew between a bank's server and ours is also a property of the
/// deployment, not of the IMF.
/// </para>
/// </summary>
internal sealed class IntegrationWebhookOptions
{
    internal const string SectionName = "Integration:Webhook";

    /// <summary>
    /// Tolerated distance between the signed timestamp and our clock, in minutes, in both
    /// directions. Five is the figure M13's webhook ingest already uses, so an operator reading
    /// both sees one number.
    /// </summary>
    public int ReplayWindowMinutes { get; set; } = 5;

    internal TimeSpan ReplayWindow => TimeSpan.FromMinutes(ReplayWindowMinutes);
}
