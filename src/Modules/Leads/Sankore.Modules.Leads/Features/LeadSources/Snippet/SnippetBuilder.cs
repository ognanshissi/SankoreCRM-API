namespace Sankore.Modules.Leads.Features.LeadSources.Snippet;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Builds the embed snippet, once, for both the screen that shows it and the mail that sends it.
///
/// <para>
/// <b>Why one builder.</b> The two handlers resolved the SDK differently: the screen read the
/// current <see cref="SdkVersion"/> from the database, the mail used
/// <see cref="SnippetOptions"/> — whose defaults are <c>/sdk/v1/forms.min.js</c> and the literal
/// <c>sha384-placeholder</c>. An emailed snippet therefore carried an integrity hash that matches
/// nothing, and a browser refuses a script whose <c>integrity</c> fails: the snippet we mail to
/// integrators could never have worked unless both settings happened to be configured by hand.
/// </para>
///
/// <para>
/// <b>Why the URL is the EXACT version, not the <c>/sdk/v{major}/</c> alias.</b> The snippet
/// carries an <c>integrity</c> hash, and an SRI hash is only meaningful against immutable
/// content. Pointing it at the alias — which resolves to whichever version is current — meant
/// that promoting a new SDK build broke every snippet already pasted into a customer's site:
/// same URL, new bytes, hash no longer matches, script refused, form gone. Pinning pairs the two
/// for good. The cost is that a site adopts a new SDK when its snippet is copied again, which is
/// the normal bargain for an embedded widget and the reason the alias still exists for anyone
/// who prefers auto-update without SRI.
/// </para>
/// </summary>
internal static class SnippetBuilder
{
    internal const string DefaultContainerId = "sankore-form";

    internal sealed record Snippet(string Html, string SdkUrl, string SriHash, string ContainerId);

    /// <summary>
    /// The SDK build new snippets pin: the current version for the highest major, or the
    /// configured fallback when the table is empty (it is seeded at start-up, so that is a
    /// first-boot case).
    /// </summary>
    internal static async Task<(string Url, string Hash)> ResolveSdkAsync(
        LeadsDbContext db, SnippetOptions options, CancellationToken ct)
    {
        var current = await db.SdkVersions
            .IgnoreQueryFilters()
            .Where(v => v.IsCurrent)
            .OrderByDescending(v => v.Major)
            .Select(v => new { v.Version, v.SriHash })
            .FirstOrDefaultAsync(ct);

        return current is not null
            ? ($"/sdk/{current.Version}/forms.min.js", current.SriHash)
            : (options.SdkUrl, options.SriHash);
    }

    internal static Snippet Build(LeadSourceConfig source, string sdkUrl, string sriHash)
    {
        var containerId = (source.Settings as EmbeddedScriptSettings)?.FormContainerId
                          ?? DefaultContainerId;

        var html = $"""
            <!-- Sankore CRM Lead Capture — {source.Label} -->
            <div id="{containerId}"></div>
            <script src="{sdkUrl}"
                    integrity="{sriHash}"
                    crossorigin="anonymous"
                    data-key="{source.PublicKey}"
                    data-container="#{containerId}"
                    defer></script>
            """;

        return new Snippet(html, sdkUrl, sriHash, containerId);
    }
}
