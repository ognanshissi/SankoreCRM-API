using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;

namespace Sankore.Admin.Features.TenantSignups;

// The website's free-trial form, decided HERE (owner, 2026-11-12). Sankore.Api is only the public
// door: it limits the rate, finds the visitor's address, relays the form to these routes with the
// service key, sends back the answer it gets, and queues the e-mails listed in it through its
// Notifications module — the registry has no mail of its own. Everything else lives here: the
// captcha, the confirmation token, what the PUBLIC may learn, which e-mails leave and to whom.
//
// Two answers are deliberately the same:
// - a new address and an address already in use both get 202 CONFIRMATION_SENT; the second receives
//   an e-mail saying a CRM or a request already exists. Otherwise the form would tell anyone which
//   addresses administer a CRM;
// - a name already taken IS said (the website checks availability as you type): names are public by
//   nature, they are addresses on the web.

/// <summary>What the website's form sends, relayed as is by Sankore.Api.</summary>
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

/// <summary>An e-mail the API must queue, under the platform's own account, as it is described.</summary>
/// <param name="TemplateKey">One of the <c>trial.*</c> templates seeded by the Notifications module.</param>
/// <param name="IdempotencyKey">The outbox key: a relay retried after a timeout mails nobody twice.</param>
public sealed record TrialMail(
    string TemplateKey,
    string To,
    string? Name,
    string Locale,
    IReadOnlyDictionary<string, string> Data,
    string IdempotencyKey);

/// <summary>
/// The whole answer to a public route: the status and body the website gets, and the e-mails to
/// queue. Body is <c>{ status, fqdn }</c> on success, <c>{ error, detail, errors }</c> otherwise —
/// the shape the website has always read.
/// </summary>
public sealed record TrialPublicAnswer(int StatusCode, object Body, IReadOnlyList<TrialMail> Mails)
{
    public static TrialPublicAnswer Ok(int statusCode, string status, string? fqdn = null,
        IReadOnlyList<TrialMail>? mails = null) =>
        new(statusCode, new { status, fqdn }, mails ?? []);

    public static TrialPublicAnswer Fail(int statusCode, string error, string? detail = null,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new(statusCode, new { error, detail, errors }, []);
}

public static class TrialPublicCodes
{
    /// <summary>The ONE success of a request, whether a new address or one already in use.</summary>
    public const string ConfirmationSent = "CONFIRMATION_SENT";
    public const string Confirmed = "CONFIRMED";

    public const string CaptchaFailed = "TRIAL_CAPTCHA_FAILED";
    public const string CaptchaNotConfigured = "TRIAL_CAPTCHA_NOT_CONFIGURED";
}

public sealed class TurnstileOptions
{
    /// <summary>
    /// Cloudflare Turnstile's SECRET key (the site key is the website's). Required outside Development:
    /// without it every request answers 503 <c>TRIAL_CAPTCHA_NOT_CONFIGURED</c> — fail closed, an
    /// open form would let robots create requests. Cloudflare's test secret
    /// <c>1x0000000000000000000000000000000AA</c> always passes.
    /// </summary>
    public string SecretKey { get; set; } = "";

    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
}

public enum CaptchaOutcome { Passed, Failed, NotConfigured }

public interface ITurnstileVerifier
{
    Task<CaptchaOutcome> VerifyAsync(string? token, string? remoteIp, CancellationToken ct);
}

/// <summary>
/// Cloudflare's server-side check of a Turnstile token. A token is single-use and lives five minutes,
/// so it is checked once, here, before anything is written. An unreachable Cloudflare is a failure,
/// never a pass.
/// </summary>
internal sealed class TurnstileVerifier(
    IHttpClientFactory httpClientFactory,
    IOptions<TenantSignupOptions> options,
    IHostEnvironment environment,
    ILogger<TurnstileVerifier> logger) : ITurnstileVerifier
{
    public const string HttpClientName = "Turnstile";

    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);

    public async Task<CaptchaOutcome> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        var turnstile = options.Value.Turnstile;
        if (string.IsNullOrWhiteSpace(turnstile.SecretKey))
        {
            if (environment.IsDevelopment())
            {
                logger.LogWarning("TrialSignup:Turnstile:SecretKey is empty: captcha skipped (Development only)");
                return CaptchaOutcome.Passed;
            }

            logger.LogError("TrialSignup:Turnstile:SecretKey is empty: free-trial requests are refused");
            return CaptchaOutcome.NotConfigured;
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) return CaptchaOutcome.Failed;

        try
        {
            var form = new Dictionary<string, string> { ["secret"] = turnstile.SecretKey, ["response"] = token };
            if (!string.IsNullOrWhiteSpace(remoteIp)) form["remoteip"] = remoteIp;

            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .PostAsync(turnstile.VerifyUrl, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Turnstile answered {Status}", (int)response.StatusCode);
                return CaptchaOutcome.Failed;
            }

            var body = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(ct);
            if (body?.Success == true) return CaptchaOutcome.Passed;

            logger.LogInformation("Turnstile refused a token: {Codes}", string.Join(",", body?.ErrorCodes ?? []));
            return CaptchaOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogWarning(ex, "Turnstile unreachable: the request is refused");
            return CaptchaOutcome.Failed;
        }
    }
}

/// <summary>The public side of the free trial: see the comment at the top of this file.</summary>
public sealed class TrialPublicService(
    ISender sender,
    ITurnstileVerifier captcha,
    IOptions<TenantSignupOptions> options,
    ILogger<TrialPublicService> logger)
{
    public async Task<TrialPublicAnswer> CheckSlugAsync(string? slug, CancellationToken ct)
    {
        var result = await sender.Send(new CheckTrialSlugQuery(slug), ct);
        return result.IsSuccess
            ? TrialPublicAnswer.Ok(StatusCodes.Status200OK, result.Value!.Status, result.Value.Fqdn)
            : NotConfigured();
    }

    public async Task<TrialPublicAnswer> SubmitAsync(TrialSignupForm form, string? clientIp, string? userAgent,
        CancellationToken ct)
    {
        var locale = LanguageCode.Normalize(form.Locale);
        if (options.Value.ValidTemplate() is null || ConfirmationUrlFor(locale) is null) return NotConfigured();

        if (!form.AcceptTerms)
            return TrialPublicAnswer.Fail(StatusCodes.Status400BadRequest, TenantSignupErrors.Invalid,
                "Certains champs sont incorrects.",
                new Dictionary<string, string> { ["acceptTerms"] = "Acceptez les conditions de l'essai." });

        switch (await captcha.VerifyAsync(form.CaptchaToken, clientIp, ct))
        {
            case CaptchaOutcome.NotConfigured:
                return TrialPublicAnswer.Fail(StatusCodes.Status503ServiceUnavailable, TrialPublicCodes.CaptchaNotConfigured);
            case CaptchaOutcome.Failed:
                return TrialPublicAnswer.Fail(StatusCodes.Status400BadRequest, TrialPublicCodes.CaptchaFailed,
                    "La vérification anti-robot a échoué. Rechargez la page et réessayez.");
        }

        var token = NewToken();
        var result = await sender.Send(new CreateTenantSignupCommand(new CreateTenantSignupEndpoint.Request(
            form.InstitutionName ?? "", form.Slug ?? "", form.FirstName ?? "", form.LastName ?? "", form.Email ?? "",
            form.Phone ?? "", form.Country ?? "", form.ActivityType ?? "", HashOf(token), clientIp, userAgent, locale)), ct);

        if (result.IsSuccess)
        {
            var created = result.Value!;
            var mail = new TrialMail("trial.signup-confirm", form.Email!.Trim(), form.FirstName?.Trim(), locale,
                new Dictionary<string, string>
                {
                    ["first_name"] = form.FirstName?.Trim() ?? "",
                    ["institution_name"] = form.InstitutionName?.Trim() ?? "",
                    ["crm_address"] = created.Fqdn,
                    ["confirm_url"] = $"{ConfirmationUrlFor(locale)}#t={token}",
                    ["expires_hours"] = ((int)Math.Round(options.Value.ConfirmationWindow.TotalHours))
                        .ToString(CultureInfo.InvariantCulture),
                },
                $"trial.signup-confirm:{created.Id}");
            return TrialPublicAnswer.Ok(StatusCodes.Status202Accepted, TrialPublicCodes.ConfirmationSent, mails: [mail]);
        }

        return result.Error switch
        {
            TenantSignupErrors.EmailInUse => AlreadyInUse(form.Email!, locale),
            TenantSignupErrors.Invalid => TrialPublicAnswer.Fail(StatusCodes.Status400BadRequest, TenantSignupErrors.Invalid,
                result.Detail, result.Errors),
            TrialSlug.Invalid or TrialSlug.Reserved => TrialPublicAnswer.Fail(StatusCodes.Status400BadRequest, result.Error,
                result.Detail, new Dictionary<string, string> { ["slug"] = result.Detail ?? "Adresse refusée." }),
            TrialSlug.Taken => TrialPublicAnswer.Fail(StatusCodes.Status409Conflict, result.Error, result.Detail,
                new Dictionary<string, string> { ["slug"] = "Cette adresse est déjà prise." }),
            _ => NotConfigured(),
        };
    }

    /// <summary>
    /// The link of the confirmation e-mail. A second click answers the same success and tells the
    /// platform nothing more: the team hears of a request once.
    /// </summary>
    public async Task<TrialPublicAnswer> ConfirmAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
            return TrialPublicAnswer.Fail(StatusCodes.Status404NotFound, TenantSignupErrors.TokenUnknown);

        var result = await sender.Send(new ConfirmTenantSignupCommand(HashOf(token.Trim())), ct);
        if (!result.IsSuccess)
        {
            return result.Error switch
            {
                TenantSignupErrors.TokenUnknown => TrialPublicAnswer.Fail(StatusCodes.Status404NotFound, result.Error,
                    "Ce lien n'est pas valable."),
                TenantSignupErrors.TokenExpired => TrialPublicAnswer.Fail(StatusCodes.Status410Gone, result.Error,
                    "Ce lien a expiré. Refaites une demande depuis le site."),
                _ => TrialPublicAnswer.Fail(StatusCodes.Status409Conflict, TenantSignupErrors.AlreadyDecided,
                    "Cette demande a déjà été traitée."),
            };
        }

        var confirmed = result.Value!;
        return TrialPublicAnswer.Ok(StatusCodes.Status200OK, TrialPublicCodes.Confirmed,
            mails: confirmed.NewlyConfirmed ? PlatformNotices(confirmed) : []);
    }

    private static TrialPublicAnswer AlreadyInUse(string email, string locale)
    {
        var address = email.Trim().ToLowerInvariant();
        // One such e-mail a day per address: the form must not become a way to flood a mailbox.
        var mail = new TrialMail("trial.signup-existing", address, null, locale, new Dictionary<string, string>(),
            $"trial.signup-existing:{address}:{DateTimeOffset.UtcNow:yyyyMMdd}");
        return TrialPublicAnswer.Ok(StatusCodes.Status202Accepted, TrialPublicCodes.ConfirmationSent, mails: [mail]);
    }

    private List<TrialMail> PlatformNotices(ConfirmTenantSignupEndpoint.Response signup)
    {
        var recipients = options.Value.PlatformRecipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (recipients.Count == 0)
        {
            logger.LogWarning("No {Section}:PlatformRecipients configured: confirmed trial request {SignupId} not mailed",
                TenantSignupOptions.SectionName, signup.Id);
            return [];
        }

        var suffix = options.Value.ValidTemplate();
        var data = new Dictionary<string, string>
        {
            ["institution_name"] = signup.InstitutionName,
            ["full_name"] = $"{signup.FirstName} {signup.LastName}".Trim(),
            ["email"] = signup.Email,
            ["phone"] = signup.Phone,
            ["country"] = signup.Country,
            ["activity_type"] = signup.ActivityType,
            ["crm_address"] = suffix is null ? signup.Slug : TenantSignupOptions.OriginFor(suffix, signup.Slug),
        };
        return recipients
            .Select(r => new TrialMail("trial.signup-received", r, null, "fr", data,
                $"trial.signup-received:{signup.Id}:{r.ToLowerInvariant()}"))
            .ToList();
    }

    private string? ConfirmationUrlFor(string locale)
    {
        var url = locale == "en" && !string.IsNullOrWhiteSpace(options.Value.ConfirmationUrlEn)
            ? options.Value.ConfirmationUrlEn
            : options.Value.ConfirmationUrl;
        return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
    }

    private static TrialPublicAnswer NotConfigured() =>
        TrialPublicAnswer.Fail(StatusCodes.Status503ServiceUnavailable, TenantSignupErrors.NotConfigured);

    /// <summary>32 random bytes, URL-safe: what travels in the e-mail's link fragment.</summary>
    internal static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Lower-case hex SHA-256 of the token: the only form the registry ever stores.</summary>
    internal static string HashOf(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// <c>/api/v1/tenant-signups/public/…</c> — the three routes Sankore.Api relays the website to, with
/// the service key. Each answers 200 with a <see cref="TrialPublicAnswer"/>: the status inside it is
/// the website's, not this call's.
/// </summary>
public static class TrialPublicEndpoints
{
    public sealed record SubmitRequest(TrialSignupForm Form, string? ClientIp, string? UserAgent);

    public sealed record ConfirmRequest(string? Token);

    public static void Map(RouteGroupBuilder group)
    {
        var pub = group.MapGroup("/public");

        pub.MapGet("/slug-availability", async ([FromQuery] string? slug, TrialPublicService service, CancellationToken ct) =>
                Results.Ok(await service.CheckSlugAsync(slug, ct)))
            .WithName("CheckTrialSlugForPublic")
            .WithSummary("The website's availability check, as the website must read it")
            .Produces<TrialPublicAnswer>()
            .WithOpenApi();

        pub.MapPost("/submit", async (SubmitRequest body, TrialPublicService service, CancellationToken ct) =>
                Results.Ok(await service.SubmitAsync(body.Form, body.ClientIp, body.UserAgent, ct)))
            .WithName("SubmitTrialSignupForPublic")
            .WithSummary("The website's form: captcha, request and the confirmation e-mail to queue")
            .Produces<TrialPublicAnswer>()
            .WithOpenApi();

        pub.MapPost("/confirm", async (ConfirmRequest body, TrialPublicService service, CancellationToken ct) =>
                Results.Ok(await service.ConfirmAsync(body.Token, ct)))
            .WithName("ConfirmTrialSignupForPublic")
            .WithSummary("The confirmation link, by its clear token, and the team's notice to queue")
            .Produces<TrialPublicAnswer>()
            .WithOpenApi();
    }
}
