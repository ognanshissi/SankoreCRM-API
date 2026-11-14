namespace Sankore.Modules.Leads.Features.Ingestion.Web;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal enum CaptchaOutcome { Passed, Failed, NotConfigured }

/// <summary>
/// Checks a web form's captcha token with its provider, using the SOURCE's secret key (vault,
/// <see cref="CaptchaSecrets.Name"/>).
///
/// <para>
/// It replaced a stub that answered true to everything (2026-11-14): a source configured with a
/// captcha accepted any token, an empty one included, so the setting protected nothing.
/// </para>
/// </summary>
internal interface ICaptchaValidator
{
    Task<CaptchaOutcome> ValidateAsync(string provider, string? secret, string token, string remoteIp, CancellationToken ct);
}

internal static class CaptchaSecrets
{
    /// <summary>The vault name of a source's captcha secret key (scope « LeadSource », the source id).</summary>
    public const string Name = "captchaSecret";
}

/// <summary>
/// Turnstile, hCaptcha and reCAPTCHA share one protocol: POST secret + response (+ remoteip) as a
/// form, answer <c>{ "success": bool }</c>; reCAPTCHA v3 adds a score. Fail closed: an unreachable
/// provider, an unknown one or a missing secret refuses the lead — except a missing secret in
/// Development, as the free-trial check does, so a local run needs no Cloudflare account.
/// </summary>
internal sealed class SiteVerifyCaptchaValidator(
    IHttpClientFactory httpClientFactory,
    IHostEnvironment environment,
    ILogger<SiteVerifyCaptchaValidator> logger) : ICaptchaValidator
{
    public const string HttpClientName = "LeadCaptcha";

    /// <summary>Below this reCAPTCHA v3 score the visitor is treated as a robot (Google's own default).</summary>
    public const double RecaptchaMinScore = 0.5;

    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("score")] double? Score,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);

    internal static string? VerifyUrl(string provider) => provider switch
    {
        "Turnstile" => "https://challenges.cloudflare.com/turnstile/v0/siteverify",
        "HCaptcha" => "https://api.hcaptcha.com/siteverify",
        "RecaptchaV3" => "https://www.google.com/recaptcha/api/siteverify",
        _ => null,
    };

    public async Task<CaptchaOutcome> ValidateAsync(
        string provider, string? secret, string token, string remoteIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (environment.IsDevelopment())
            {
                logger.LogWarning("Lead source captcha {Provider} has no secret key: check skipped (Development only)", provider);
                return CaptchaOutcome.Passed;
            }
            logger.LogError("Lead source captcha {Provider} has no secret key: the lead is refused", provider);
            return CaptchaOutcome.NotConfigured;
        }

        var url = VerifyUrl(provider);
        if (url is null)
        {
            logger.LogError("Unknown captcha provider {Provider}: the lead is refused", provider);
            return CaptchaOutcome.NotConfigured;
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return CaptchaOutcome.Failed;

        try
        {
            var form = new Dictionary<string, string> { ["secret"] = secret, ["response"] = token };
            if (!string.IsNullOrWhiteSpace(remoteIp) && remoteIp != "unknown") form["remoteip"] = remoteIp;

            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .PostAsync(url, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Captcha provider {Provider} answered {Status}", provider, (int)response.StatusCode);
                return CaptchaOutcome.Failed;
            }

            var body = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(ct);
            var passed = body?.Success == true
                         && (provider != "RecaptchaV3" || (body.Score ?? 0) >= RecaptchaMinScore);
            if (passed) return CaptchaOutcome.Passed;

            logger.LogInformation("Captcha provider {Provider} refused a token: {Codes} (score {Score})",
                provider, string.Join(",", body?.ErrorCodes ?? []), body?.Score);
            return CaptchaOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogWarning(ex, "Captcha provider {Provider} unreachable: the lead is refused", provider);
            return CaptchaOutcome.Failed;
        }
    }
}
