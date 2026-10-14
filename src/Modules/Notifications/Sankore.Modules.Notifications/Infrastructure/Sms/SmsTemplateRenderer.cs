namespace Sankore.Modules.Notifications.Infrastructure.Sms;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;
using Sankore.Modules.Notifications.Domain;
using Sankore.Shared.Kernel;

/// <summary>Renders an SMS body for a tenant, a locale and a payload.</summary>
internal interface ISmsTemplateRenderer
{
    Task<RenderedSms> RenderAsync(
        Guid tenantId, string templateKey, string locale, string templateDataJson,
        CancellationToken ct = default);
}

/// <param name="SegmentCount">
/// How many SMS segments the body takes. Carried because it IS the bill — a 161-character message
/// costs twice a 160-character one — and because a template that quietly grew past the boundary is
/// otherwise invisible until an invoice arrives.
/// </param>
/// <param name="Found">
/// False when no template matched and the body is a fallback. The processor refuses to SEND in
/// that case: an email falling back to its own JSON payload is embarrassing, an SMS doing it costs
/// money per attempt and tells a customer nothing.
/// </param>
internal sealed record RenderedSms(string Body, int SegmentCount, bool Found);

/// <summary>
/// The SMS twin of <c>ScribanTemplateRenderer</c>, with the same resolution order — tenant row,
/// platform row in the requested locale, platform row in French — so one mental model covers both
/// channels.
///
/// <para>
/// <b>Where it deliberately differs: a missing template does not render a fallback body.</b> The
/// email renderer returns the payload as a <c>&lt;pre&gt;</c> block and lets the message go,
/// which is recoverable — somebody receives an ugly email. Here the same behaviour would spend a
/// tenant's money to send a customer a JSON fragment, so this one reports
/// <see cref="RenderedSms.Found"/> false and the processor dead-letters the message with a reason
/// an operator can act on.
/// </para>
/// </summary>
internal sealed class SmsTemplateRenderer(
    NotificationsDbContext db,
    ILogger<SmsTemplateRenderer> logger)
    : ISmsTemplateRenderer
{
    private const string DefaultLocale = LanguageCode.Default;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<RenderedSms> RenderAsync(
        Guid tenantId, string templateKey, string locale, string templateDataJson,
        CancellationToken ct = default)
    {
        // Normalised for the reason the email renderer documents: locales are stored lower-case
        // and matched in PostgreSQL, where string equality is case-sensitive — so "FR" matches
        // nothing and the message would go out as its own payload.
        var normalizedLocale = LanguageCode.Normalize(locale);

        var template = await ResolveAsync(tenantId, templateKey, normalizedLocale, ct);

        if (template is null)
        {
            logger.LogError(
                "No active SmsTemplate for Key={Key} Locale={Locale} Tenant={TenantId} — "
                + "the message will not be sent",
                templateKey, normalizedLocale, tenantId);

            return new RenderedSms(string.Empty, 0, Found: false);
        }

        var scriptObject = BuildScriptObject(templateDataJson);
        var context = new TemplateContext { LoopLimit = 100, RecursiveLimit = 10 };
        context.PushGlobal(scriptObject);

        var body = Render(template.Body, context, templateKey);

        return new RenderedSms(body, SmsSegments.Count(body), Found: true);
    }

    private async Task<SmsTemplate?> ResolveAsync(
        Guid tenantId, string key, string locale, CancellationToken ct)
    {
        // 1. The tenant's own override, in the requested locale.
        var found = await db.SmsTemplates
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                t => t.TenantId == tenantId && t.TemplateKey == key
                  && t.Locale == locale && t.IsActive,
                ct);

        if (found is not null) return found;

        // 2. The platform default, same locale.
        found = await db.SmsTemplates
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                t => t.TenantId == null && t.TemplateKey == key
                  && t.Locale == locale && t.IsActive,
                ct);

        if (found is not null) return found;

        // 3. The platform default in French, the one locale every key is seeded in.
        if (locale != DefaultLocale)
        {
            found = await db.SmsTemplates
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    t => t.TenantId == null && t.TemplateKey == key
                      && t.Locale == DefaultLocale && t.IsActive,
                    ct);
        }

        return found;
    }

    private static ScriptObject BuildScriptObject(string templateDataJson)
    {
        var scriptObject = new ScriptObject();

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                templateDataJson, JsonOpts);

            if (data is not null)
            {
                foreach (var (k, v) in data)
                    scriptObject[k] = Unwrap(v);
            }
        }
        catch (JsonException)
        {
            // A payload that will not parse leaves the globals empty, so the template renders
            // with its placeholders unfilled rather than with the raw JSON inlined. On this
            // channel that is the lesser of the two: a message missing a name is readable, a
            // message containing a JSON fragment is not.
            scriptObject.Clear();
        }

        return scriptObject;
    }

    private static object? Unwrap(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number when el.TryGetInt64(out var i) => i,
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(Unwrap).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => Unwrap(p.Value)),
        _ => el.ToString(),
    };

    private string Render(string source, TemplateContext context, string templateKey)
    {
        var parsed = Template.Parse(source);

        if (parsed.HasErrors)
        {
            logger.LogError(
                "Scriban parse error in SmsTemplate Key={Key}: {Errors}",
                templateKey, string.Join("; ", parsed.Messages));

            // The raw source rather than a crash — same posture as the email renderer. It is a
            // bad message, and the alternative is the outbox retrying a template that will never
            // parse until somebody edits it.
            return source;
        }

        return parsed.Render(context);
    }
}
