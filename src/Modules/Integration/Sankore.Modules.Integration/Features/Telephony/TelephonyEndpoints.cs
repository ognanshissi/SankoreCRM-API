namespace Sankore.Modules.Integration.Features.Telephony;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.GetDirectory;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.ListUserLinks;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.SetCallerNumber;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.UnlinkUser;
using Sankore.Modules.Integration.Features.Telephony.Webhook;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Area aggregator for the telephony family: one MapGroup, one call per slice (TEL-B-20).
///
/// <para>
/// <c>integration/telephony</c> and not a top-level <c>telephony</c> group, although the family
/// is agent-facing rather than back-office: the connection, the adapter, the secrets and the
/// webhook all live under <c>integration</c>, and a second root would hide which module owns the
/// provider — which is the first thing somebody debugging a missed call needs to know.
/// </para>
/// </summary>
internal static class TelephonyEndpoints
{
    internal static IEndpointRouteBuilder MapTelephonyEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("integration/telephony").WithTags("Telephony");

        // ── User links (TEL-B-05) ────────────────────────────────────────────
        // The directory is read live from the provider; everything else writes this module's own
        // rows and keeps working while the provider is down — which matters, because a provider
        // change is exactly what breaks a link somebody has to repair.
        group.MapGetTelephonyDirectory();
        group.MapListTelephonyUserLinks();
        group.MapLinkTelephonyUser();
        group.MapSetTelephonyCallerNumber();
        group.MapUnlinkTelephonyUser();

        // ── The webhook simulator (TEL-B-18, §9) ────────────────────────────
        // Mapped in Development only. There is no Ringover account in this deployment, so this is
        // the only way to drive a call through the journal end to end — and the mapping guard is
        // doubled by a runtime check inside the endpoint, because a mapping is what a refactor
        // moves.
        if (EnvironmentOf(app) is { IsDevelopment: true })
            group.MapSimulateTelephonyWebhook();

        return app;
    }

    /// <summary>
    /// The host environment, read off the route builder's own services. Null when the builder
    /// exposes none, in which case the simulator is simply not mapped — the conservative reading.
    /// </summary>
    private static DevelopmentFlag? EnvironmentOf(IEndpointRouteBuilder app)
        => app.ServiceProvider.GetService(typeof(IHostEnvironment)) is IHostEnvironment env
            ? new DevelopmentFlag(env.IsDevelopment())
            : null;

    /// <summary>
    /// A one-field carrier so the condition above reads as a property pattern. Named rather than
    /// a bare bool because a null answer and a false one mean different things here: no
    /// environment service at all (a route builder that exposes none) versus a host that is not
    /// in Development — both decline to map, and the distinction is worth keeping visible.
    /// </summary>
    private sealed record DevelopmentFlag(bool IsDevelopment);
}
