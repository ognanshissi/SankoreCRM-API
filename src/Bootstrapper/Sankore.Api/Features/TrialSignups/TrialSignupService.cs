using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

namespace Sankore.Api.Features.TrialSignups;

/// <summary>What the website's form sends (<c>docs/essai-gratuit-socle.md</c>, lot 2).</summary>
public sealed record TrialSignupForm(
    string? InstitutionName,
    string? Slug,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    string? Country,
    string? ActivityType,
    bool AcceptTerms,
    string? CaptchaToken,
    string? Locale);

/// <summary>An answer to the public: a status code, and the body the website reads.</summary>
public sealed record TrialPublicResult(int StatusCode, string? Status = null, string? Error = null, string? Detail = null,
    IReadOnlyDictionary<string, string>? Errors = null);

public static class TrialPublicCodes
{
    /// <summary>The ONE success of a request, whether a new address or one already in use.</summary>
    public const string ConfirmationSent = "CONFIRMATION_SENT";
    public const string Confirmed = "CONFIRMED";

    public const string CaptchaFailed = "TRIAL_CAPTCHA_FAILED";
    public const string CaptchaNotConfigured = "TRIAL_CAPTCHA_NOT_CONFIGURED";
    public const string NotConfigured = "TRIAL_SIGNUP_NOT_CONFIGURED";
    public const string Unavailable = "TRIAL_SIGNUP_UNAVAILABLE";
    public const string Invalid = "TRIAL_SIGNUP_INVALID";
}

/// <summary>
/// The public side of the free trial. The registry validates and stores; this service adds what only
/// the API can do — the captcha, the confirmation token, the e-mails — and decides what the PUBLIC
/// may learn. Two answers are deliberately the same:
/// <list type="bullet">
/// <item>a new address and an address already in use both get 202 <see cref="TrialPublicCodes.ConfirmationSent"/>;
/// the second receives an e-mail saying a CRM or a request already exists. Otherwise the form would
/// tell anyone which addresses administer a CRM;</item>
/// <item>a name already taken IS said (the website checks availability as you type): names are
/// public by nature, they are addresses on the web.</item>
/// </list>
/// </summary>
public sealed class TrialSignupService(
    ITrialSignupRegistry registry,
    ITurnstileVerifier captcha,
    INotificationsModule notifications,
    IOptions<TrialSignupOptions> options,
    ILogger<TrialSignupService> logger)
{
    /// <summary>
    /// The tenant of the e-mails sent before any institution exists. No tenant has this id, so the
    /// outbox resolves the platform's own e-mail account, applies no quota and no test-mode filter.
    /// </summary>
    public static readonly Guid PlatformTenantId = Guid.Empty;

    public async Task<TrialPublicResult> CheckSlugAsync(string? slug, CancellationToken ct)
    {
        var call = await registry.CheckSlugAsync(slug, ct);
        if (!call.IsSuccess) return Outage(call.Error);
        return new TrialPublicResult(StatusCodes.Status200OK, call.Value!.Status, Detail: call.Value.Fqdn);
    }

    public async Task<TrialPublicResult> SubmitAsync(TrialSignupForm form, string? clientIp, string? userAgent,
        CancellationToken ct)
    {
        if (ConfirmationUrlFor(LanguageCode.Normalize(form.Locale)) is null)
            return new TrialPublicResult(StatusCodes.Status503ServiceUnavailable, Error: TrialPublicCodes.NotConfigured);

        if (!form.AcceptTerms)
            return new TrialPublicResult(StatusCodes.Status400BadRequest, Error: TrialPublicCodes.Invalid,
                Detail: "Certains champs sont incorrects.",
                Errors: new Dictionary<string, string> { ["acceptTerms"] = "Acceptez les conditions de l'essai." });

        switch (await captcha.VerifyAsync(form.CaptchaToken, clientIp, ct))
        {
            case CaptchaOutcome.NotConfigured:
                return new TrialPublicResult(StatusCodes.Status503ServiceUnavailable, Error: TrialPublicCodes.CaptchaNotConfigured);
            case CaptchaOutcome.Failed:
                return new TrialPublicResult(StatusCodes.Status400BadRequest, Error: TrialPublicCodes.CaptchaFailed,
                    Detail: "La vérification anti-robot a échoué. Rechargez la page et réessayez.");
        }

        var locale = LanguageCode.Normalize(form.Locale);
        var token = NewToken();
        var call = await registry.CreateAsync(new RegistrySignupRequest(
            form.InstitutionName ?? "", form.Slug ?? "", form.FirstName ?? "", form.LastName ?? "", form.Email ?? "",
            form.Phone ?? "", form.Country ?? "", form.ActivityType ?? "", HashOf(token), clientIp, userAgent, locale), ct);

        if (call.IsSuccess)
        {
            await QueueAsync("trial.signup-confirm", form.Email!.Trim(), form.FirstName, locale,
                new Dictionary<string, object>
                {
                    ["first_name"] = form.FirstName?.Trim() ?? "",
                    ["institution_name"] = form.InstitutionName?.Trim() ?? "",
                    ["crm_address"] = call.Value!.Fqdn,
                    ["confirm_url"] = $"{ConfirmationUrlFor(locale)}#t={token}",
                    ["expires_hours"] = options.Value.ConfirmationHours,
                },
                $"trial.signup-confirm:{call.Value.Id}", ct);
            return new TrialPublicResult(StatusCodes.Status202Accepted, TrialPublicCodes.ConfirmationSent);
        }

        return call.Error switch
        {
            "TRIAL_EMAIL_IN_USE" => await AlreadyInUseAsync(form.Email!, locale, ct),
            "TRIAL_SIGNUP_INVALID" => new TrialPublicResult(StatusCodes.Status400BadRequest, Error: TrialPublicCodes.Invalid,
                Detail: call.Detail, Errors: call.Errors),
            TrialSlug.Invalid or TrialSlug.Reserved => new TrialPublicResult(StatusCodes.Status400BadRequest, Error: call.Error,
                Detail: call.Detail, Errors: new Dictionary<string, string> { ["slug"] = call.Detail ?? "Adresse refusée." }),
            TrialSlug.Taken => new TrialPublicResult(StatusCodes.Status409Conflict, Error: call.Error, Detail: call.Detail,
                Errors: new Dictionary<string, string> { ["slug"] = "Cette adresse est déjà prise." }),
            _ => Outage(call.Error),
        };
    }

    /// <summary>
    /// The link of the confirmation e-mail. A second click answers the same success and tells the
    /// platform nothing more: the team hears of a request once.
    /// </summary>
    public async Task<TrialPublicResult> ConfirmAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
            return new TrialPublicResult(StatusCodes.Status404NotFound, Error: "TRIAL_TOKEN_UNKNOWN");

        var call = await registry.ConfirmAsync(HashOf(token.Trim()), ct);
        if (!call.IsSuccess)
        {
            return call.Error switch
            {
                "TRIAL_TOKEN_UNKNOWN" => new TrialPublicResult(StatusCodes.Status404NotFound, Error: call.Error,
                    Detail: "Ce lien n'est pas valable."),
                "TRIAL_TOKEN_EXPIRED" => new TrialPublicResult(StatusCodes.Status410Gone, Error: call.Error,
                    Detail: "Ce lien a expiré. Refaites une demande depuis le site."),
                "TRIAL_SIGNUP_ALREADY_DECIDED" => new TrialPublicResult(StatusCodes.Status409Conflict, Error: call.Error,
                    Detail: "Cette demande a déjà été traitée."),
                _ => Outage(call.Error),
            };
        }

        if (call.Value!.NewlyConfirmed) await TellPlatformAsync(call.Value, ct);
        return new TrialPublicResult(StatusCodes.Status200OK, TrialPublicCodes.Confirmed);
    }

    private async Task<TrialPublicResult> AlreadyInUseAsync(string email, string locale, CancellationToken ct)
    {
        var address = email.Trim().ToLowerInvariant();
        // One such e-mail a day per address: the form must not become a way to flood a mailbox.
        await QueueAsync("trial.signup-existing", address, null, locale, new Dictionary<string, object>(),
            $"trial.signup-existing:{address}:{DateTimeOffset.UtcNow:yyyyMMdd}", ct);
        return new TrialPublicResult(StatusCodes.Status202Accepted, TrialPublicCodes.ConfirmationSent);
    }

    private async Task TellPlatformAsync(RegistrySignupConfirmed signup, CancellationToken ct)
    {
        var recipients = options.Value.PlatformRecipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (recipients.Count == 0)
        {
            logger.LogWarning("No {Section}:PlatformRecipients configured: confirmed trial request {SignupId} not mailed",
                TrialSignupOptions.SectionName, signup.Id);
            return;
        }

        foreach (var recipient in recipients)
            await QueueAsync("trial.signup-received", recipient, null, "fr", new Dictionary<string, object>
                {
                    ["institution_name"] = signup.InstitutionName,
                    ["full_name"] = $"{signup.FirstName} {signup.LastName}".Trim(),
                    ["email"] = signup.Email,
                    ["phone"] = signup.Phone,
                    ["country"] = signup.Country,
                    ["activity_type"] = signup.ActivityType,
                    ["crm_address"] = signup.Slug,
                },
                $"trial.signup-received:{signup.Id}:{recipient.ToLowerInvariant()}", ct);
    }

    private async Task QueueAsync(string template, string to, string? name, string locale, Dictionary<string, object> data,
        string key, CancellationToken ct)
    {
        var queued = await notifications.QueueEmailAsync(new QueueEmailRequest(
            TemplateKey: template, RecipientEmail: to, RecipientName: name?.Trim(), Module: "TrialSignup",
            Locale: locale, TemplateData: data, IdempotencyKey: key, TenantId: PlatformTenantId), ct);
        if (queued.IsFailure && queued.Error != "DUPLICATE_IDEMPOTENCY_KEY")
            logger.LogError("Could not queue '{Template}' ({Key}): {Error}", template, key, queued.Error);
    }

    private string? ConfirmationUrlFor(string locale)
    {
        var url = locale == "en" && !string.IsNullOrWhiteSpace(options.Value.ConfirmationUrlEn)
            ? options.Value.ConfirmationUrlEn
            : options.Value.ConfirmationUrl;
        return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
    }

    private static TrialPublicResult Outage(string? error)
    {
        if (error == "TRIAL_SIGNUP_NOT_CONFIGURED")
            return new TrialPublicResult(StatusCodes.Status503ServiceUnavailable, Error: TrialPublicCodes.NotConfigured);
        return new TrialPublicResult(StatusCodes.Status503ServiceUnavailable, Error: TrialPublicCodes.Unavailable,
            Detail: "Le service est momentanément indisponible. Réessayez dans quelques minutes.");
    }

    /// <summary>32 random bytes, URL-safe: what travels in the e-mail's link fragment.</summary>
    internal static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Lower-case hex SHA-256 of the token: the only form the registry ever holds.</summary>
    internal static string HashOf(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

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
