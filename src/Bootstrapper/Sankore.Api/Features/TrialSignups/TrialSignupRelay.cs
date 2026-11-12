using System.Globalization;
using System.Net;
using System.Text.Json;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

namespace Sankore.Api.Features.TrialSignups;

/// <summary>
/// The website's free-trial form, relayed to the registry (owner, 2026-11-12). The registry
/// (Sankore.Admin, <c>tenant-signups/public/…</c>) decides everything — captcha, confirmation token,
/// what the public may learn, which e-mails leave — and answers the status and body the website must
/// get plus the e-mails to queue. This API only stays the public door, because the registry is never
/// exposed: it limits the rate, finds the visitor's address, relays, queues those e-mails under the
/// platform's account (the registry has no mail of its own) and returns the answer as it came.
/// </summary>
public sealed class TrialSignupRelay(
    ITrialSignupRegistry registry,
    INotificationsModule notifications,
    ILogger<TrialSignupRelay> logger)
{
    /// <summary>
    /// The tenant of the e-mails sent before any institution exists. No tenant has this id, so the
    /// outbox resolves the platform's own e-mail account, applies no quota and no test-mode filter.
    /// </summary>
    public static readonly Guid PlatformTenantId = Guid.Empty;

    public const string UnavailableCode = "TRIAL_SIGNUP_UNAVAILABLE";

    public async Task<IResult> CheckSlugAsync(string? slug, CancellationToken ct) =>
        await RelayAsync(await registry.CheckSlugForPublicAsync(slug, ct), ct);

    public async Task<IResult> SubmitAsync(JsonElement form, string? clientIp, string? userAgent, CancellationToken ct) =>
        await RelayAsync(await registry.SubmitForPublicAsync(form, clientIp, userAgent, ct), ct);

    public async Task<IResult> ConfirmAsync(string? token, CancellationToken ct) =>
        await RelayAsync(await registry.ConfirmForPublicAsync(token, ct), ct);

    private async Task<IResult> RelayAsync(RegistryCall<RegistryPublicAnswer> call, CancellationToken ct)
    {
        if (!call.IsSuccess || call.Value is not { } answer)
            return Results.Json(new
                {
                    error = UnavailableCode,
                    detail = "Le service est momentanément indisponible. Réessayez dans quelques minutes.",
                    errors = (object?)null,
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);

        foreach (var mail in answer.Mails ?? [])
        {
            var queued = await notifications.QueueEmailAsync(new QueueEmailRequest(
                TemplateKey: mail.TemplateKey, RecipientEmail: mail.To, RecipientName: mail.Name, Module: "TrialSignup",
                Locale: LanguageCode.Normalize(mail.Locale),
                TemplateData: (mail.Data ?? new Dictionary<string, string>()).ToDictionary(p => p.Key, p => (object)p.Value),
                IdempotencyKey: mail.IdempotencyKey, TenantId: PlatformTenantId), ct);
            if (queued.IsFailure && queued.Error != "DUPLICATE_IDEMPOTENCY_KEY")
                logger.LogError("Could not queue '{Template}' ({Key}): {Error}", mail.TemplateKey, mail.IdempotencyKey,
                    queued.Error);
        }

        return Results.Json(answer.Body, statusCode: answer.StatusCode);
    }

    /// <summary>The visitor's address. Behind Cloudflare, <c>CF-Connecting-IP</c>; otherwise the connection's.</summary>
    public static string? ClientIp(HttpContext http)
    {
        var header = http.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        return header is not null && IPAddress.TryParse(header.Trim(), out var parsed)
            ? parsed.ToString()
            : http.Connection.RemoteIpAddress?.ToString();
    }

    internal static string TrialEnd(DateTimeOffset? end, string locale) =>
        end is { } value
            ? value.ToString(locale == "en" ? "MMMM d, yyyy" : "d MMMM yyyy",
                CultureInfo.GetCultureInfo(locale == "en" ? "en-US" : "fr-FR"))
            : "";
}
