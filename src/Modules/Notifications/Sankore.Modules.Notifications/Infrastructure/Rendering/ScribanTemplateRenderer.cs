namespace Sankore.Modules.Notifications.Infrastructure.Rendering;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Infrastructure;

/// <summary>
/// Loads the active EmailTemplate from the database and renders it with Scriban.
///
/// Resolution order (first match wins):
///   1. Tenant-specific, requested locale, IsActive = true
///   2. Platform default (TenantId = null), requested locale, IsActive = true
///   3. Platform default, locale = "en", IsActive = true  (fallback)
///   4. Soft fallback: returns stub output and logs a warning (never throws).
///
/// Template variables are injected from the JSON payload as top-level Scriban globals.
/// Both the Subject and Body strings are rendered independently so subjects can also
/// contain dynamic values (e.g. "Welcome {{ first_name }}").
/// </summary>
internal sealed class ScribanTemplateRenderer(
    NotificationsDbContext db,
    ILogger<ScribanTemplateRenderer> logger)
    : ITemplateRenderer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<RenderedEmail> RenderAsync(
        Guid tenantId,
        string templateKey,
        string locale,
        string templateDataJson,
        CancellationToken ct = default)
    {
        var template = await ResolveTemplateAsync(tenantId, templateKey, locale, ct);

        if (template is null)
        {
            logger.LogWarning(
                "No active EmailTemplate found for Key={Key} Locale={Locale} Tenant={TenantId} — using stub fallback",
                templateKey, locale, tenantId);
            return new RenderedEmail(
                $"[{locale}] {templateKey}",
                $"<pre>{templateDataJson}</pre>",
                templateDataJson);
        }

        var scriptObject = BuildScriptObject(templateDataJson);
        var context = new TemplateContext { LoopLimit = 1000, RecursiveLimit = 100 };
        context.PushGlobal(scriptObject);

        var subject = RenderString(template.Subject, context, templateKey, "subject");
        var htmlBody = RenderString(template.HtmlBody, context, templateKey, "html_body");
        var textBody = template.TextBody is not null
            ? RenderString(template.TextBody, context, templateKey, "text_body")
            : null;

        return new RenderedEmail(subject, htmlBody, textBody);
    }

    // ─── Private helpers ────────────────────────────────────────────────────

    private async Task<EmailTemplate?> ResolveTemplateAsync(
        Guid tenantId, string key, string locale, CancellationToken ct)
    {
        // 1. Tenant-specific + requested locale
        var found = await db.EmailTemplates
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                t => t.TenantId == tenantId && t.TemplateKey == key && t.Locale == locale && t.IsActive,
                ct);

        if (found is not null) return found;

        // 2. Platform default + requested locale
        found = await db.EmailTemplates
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                t => t.TenantId == null && t.TemplateKey == key && t.Locale == locale && t.IsActive,
                ct);

        if (found is not null) return found;

        // 3. Platform default + "en" fallback
        if (!string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase))
        {
            found = await db.EmailTemplates
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    t => t.TenantId == null && t.TemplateKey == key && t.Locale == "en" && t.IsActive,
                    ct);
        }

        return found;
    }

    private static ScriptObject BuildScriptObject(string templateDataJson)
    {
        var scriptObject = new ScriptObject();

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(templateDataJson, JsonOpts);
            if (data is not null)
            {
                foreach (var (k, v) in data)
                    scriptObject[k] = UnwrapJsonElement(v);
            }
        }
        catch
        {
            // Non-JSON payload — expose as raw string under "data"
            scriptObject["data"] = templateDataJson;
        }

        return scriptObject;
    }

    private static object? UnwrapJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number when el.TryGetInt64(out var i) => i,
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(UnwrapJsonElement).ToList(),
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => UnwrapJsonElement(p.Value)),
        _ => el.ToString()
    };

    private string RenderString(string source, TemplateContext context, string templateKey, string part)
    {
        var parsed = Template.Parse(source);
        if (parsed.HasErrors)
        {
            logger.LogError(
                "Scriban parse error in template Key={Key} Part={Part}: {Errors}",
                templateKey, part, string.Join("; ", parsed.Messages));
            return source; // return raw source rather than crashing
        }

        return parsed.Render(context);
    }
}
