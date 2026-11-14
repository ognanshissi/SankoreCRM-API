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

    /// <summary>No public API address: the snippet would not work on a customer's site.</summary>
    internal const string PublicUrlNotConfigured = "SNIPPET_PUBLIC_URL_NOT_CONFIGURED";

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

    /// <summary>
    /// Where the snippet reaches the API from a CUSTOMER's site: <c>Leads:Snippet:PublicBaseUrl</c>
    /// when set, else — the endpoints pass it IN DEVELOPMENT ONLY — the host the CRM screen called,
    /// in https. Empty means not configured, and the handlers refuse rather than build a snippet.
    ///
    /// <para>
    /// Never the request host outside Development: <c>AllowedHosts</c> is <c>*</c>, so the Host
    /// header is whatever the caller sends, and « Envoyer au webmaster » would have the
    /// institution's own mailer send a customer a snippet loading a script from any domain.
    /// </para>
    ///
    /// <para>
    /// The snippet used to carry RELATIVE paths and no API address: pasted on a customer's site,
    /// the browser fetched <c>/sdk/…</c> from that site (404) and the SDK posted leads to that
    /// site too. It also emitted <c>data-key</c> / <c>data-container</c>, which the SDK never
    /// read (it reads <c>data-sankore-*</c>) — so no snippet ever ran outside the API's origin.
    /// </para>
    /// </summary>
    internal static string ApiBaseUrl(SnippetOptions options, string? requestHost)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
            return Uri.TryCreate(options.PublicBaseUrl.Trim(), UriKind.Absolute, out var configured)
                   && configured.Scheme is "https" or "http"
                ? configured.GetLeftPart(UriPartial.Authority)
                : string.Empty;
        return string.IsNullOrWhiteSpace(requestHost) ? string.Empty : $"https://{requestHost.Trim().TrimEnd('/')}";
    }

    internal static Snippet Build(LeadSourceConfig source, string sdkUrl, string sriHash, string apiBaseUrl = "")
    {
        var script = (source.Settings as EmbeddedScriptSettings)?.Script;
        var hosted = script?.FormMode == ScriptFormMode.Hosted;
        var absoluteSdk = Uri.IsWellFormedUriString(sdkUrl, UriKind.Absolute) ? sdkUrl : apiBaseUrl + sdkUrl;

        var attributes = new List<string>
        {
            $"src=\"{Attr(absoluteSdk)}\"",
            $"integrity=\"{Attr(sriHash)}\"",
            "crossorigin=\"anonymous\"",
            $"data-sankore-key=\"{Attr(source.PublicKey ?? string.Empty)}\"",
        };
        if (apiBaseUrl.Length > 0)
            attributes.Add($"data-sankore-api=\"{Attr(apiBaseUrl)}\"");

        string containerId;
        string? container = null;
        if (hosted)
        {
            containerId = BareId(script?.FormSelector) ?? DefaultContainerId;
            container = $"<div id=\"{Attr(containerId)}\"></div>";
            attributes.Add("data-sankore-render=\"true\"");
            attributes.Add($"data-sankore-container=\"#{Attr(containerId)}\"");
        }
        else
        {
            var selector = string.IsNullOrWhiteSpace(script?.FormSelector)
                ? "#" + DefaultContainerId
                : AsSelector(script.FormSelector);
            containerId = script?.FormSelector?.Trim() is { Length: > 0 } typed ? typed : DefaultContainerId;
            attributes.Add($"data-sankore-form=\"{Attr(selector)}\"");
            if (script is { PreventDefaultSubmit: false })
                attributes.Add("data-sankore-prevent=\"false\"");
        }

        if (script?.AfterSubmit == ScriptAfterSubmit.Redirect && !string.IsNullOrWhiteSpace(script.RedirectUrl))
            attributes.Add($"data-sankore-redirect=\"{Attr(script.RedirectUrl)}\"");
        else if (!string.IsNullOrWhiteSpace(script?.SuccessMessage))
            attributes.Add($"data-sankore-success=\"{Attr(script.SuccessMessage)}\"");

        attributes.Add("defer");

        var lines = new List<string> { $"<!-- Sankore CRM Lead Capture — {Comment(source.Label)} -->" };
        if (container is not null) lines.Add(container);
        lines.Add("<script " + string.Join("\n        ", attributes) + "></script>");

        return new Snippet(string.Join("\n", lines), absoluteSdk, sriHash, containerId);
    }

    /// <summary>A bare id (« contact-form ») becomes « #contact-form »; a selector is kept.</summary>
    private static string AsSelector(string typed)
    {
        var value = typed.Trim();
        return BareId(value) is { } id ? "#" + id : value;
    }

    private static string? BareId(string? typed)
    {
        var value = typed?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith('#') && value.Length > 1 && value[1..].All(IsIdChar)) return value[1..];
        return value.All(IsIdChar) ? value : null;
    }

    private static bool IsIdChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';

    // Seuls les caractères qui ferment ou cassent un attribut sont échappés. `WebUtility.HtmlEncode`
    // codait aussi les accents (« é » → « &#233; ») : valide, mais illisible pour le webmestre qui
    // copie le snippet et veut relire ou retoucher son message.
    private static string Attr(string value) => value
        .Replace("&", "&amp;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private static string Comment(string value) => value.Replace("--", "—").Replace(">", "›");
}
