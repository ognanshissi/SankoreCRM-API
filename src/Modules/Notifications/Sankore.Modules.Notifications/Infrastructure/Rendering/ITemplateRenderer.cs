namespace Sankore.Modules.Notifications.Infrastructure.Rendering;

/// <summary>
/// Renders an email template for a given tenant, locale, and template data.
/// Phase 4: StubTemplateRenderer (passthrough).
/// Phase 5: ScribanTemplateRenderer (loads EmailTemplate rows, renders with Scriban).
/// Resolution order: tenant-specific → platform default (same locale) → platform default (en).
/// </summary>
internal interface ITemplateRenderer
{
    Task<RenderedEmail> RenderAsync(
        Guid tenantId,
        string templateKey,
        string locale,
        string templateDataJson,
        CancellationToken ct = default);
}

/// <summary>Fully rendered email content ready for transport.</summary>
internal sealed record RenderedEmail(string Subject, string HtmlBody, string? TextBody);
