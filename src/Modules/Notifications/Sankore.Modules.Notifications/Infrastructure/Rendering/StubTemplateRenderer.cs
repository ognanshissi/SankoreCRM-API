namespace Sankore.Modules.Notifications.Infrastructure.Rendering;

/// <summary>
/// Phase 4 stub — echoes the template key as subject and the raw JSON payload as body.
/// Replaced by ScribanTemplateRenderer in Phase 5.
/// </summary>
internal sealed class StubTemplateRenderer : ITemplateRenderer
{
    public Task<RenderedEmail> RenderAsync(
        Guid tenantId,
        string templateKey,
        string locale,
        string templateDataJson,
        CancellationToken ct = default)
    {
        var subject = $"[{locale}] {templateKey}";
        var html = $"<pre>{templateDataJson}</pre>";
        return Task.FromResult(new RenderedEmail(subject, html, templateDataJson));
    }
}
