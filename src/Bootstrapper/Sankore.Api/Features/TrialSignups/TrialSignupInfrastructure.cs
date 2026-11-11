using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Tenants;

namespace Sankore.Api.Features.TrialSignups;

/// <summary>
/// The free trial's public side (<c>docs/essai-gratuit-socle.md</c>, lot 2), bound from <c>TrialSignup</c>.
/// The registry (Sankore.Admin) has its own <c>TrialSignup</c> section for the address and the length
/// of the trial; this one is what only the API needs.
/// </summary>
public sealed class TrialSignupOptions
{
    public const string SectionName = "TrialSignup";

    /// <summary>
    /// The website page that reads the token from the fragment and confirms it:
    /// <c>https://talisoft.net/essai/confirmation</c>. The token is appended as <c>#t=…</c>, so it is
    /// never sent to a server, logged by a proxy or leaked by a Referer. Empty = the public routes
    /// answer 503: a confirmation e-mail without a link would be worse than none.
    /// </summary>
    public string ConfirmationUrl { get; set; } = "";

    /// <summary>The same page of the English website (<c>https://talisoft.net/en/trial/confirmation</c>); empty = the French one.</summary>
    public string ConfirmationUrlEn { get; set; } = "";

    /// <summary>How long the confirmation link lives — said in the e-mail; the registry enforces its own.</summary>
    public int ConfirmationHours { get; set; } = 48;

    /// <summary>Requests a single IP may send per hour.</summary>
    public int RequestsPerHourPerIp { get; set; } = 5;

    /// <summary>The platform team, told of every confirmed request (D8). Empty = nobody, logged.</summary>
    public string[] PlatformRecipients { get; set; } = [];

    public TurnstileOptions Turnstile { get; set; } = new();
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
    IOptions<TrialSignupOptions> options,
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

// ── The registry's routes (Sankore.Admin, service key) ──────────────────────────────────────────

public sealed record RegistryCall<T>(T? Value, string? Error, string? Detail = null,
    IReadOnlyDictionary<string, string>? Errors = null)
{
    public const string Unavailable = "TRIAL_REGISTRY_UNAVAILABLE";
    public bool IsSuccess => Error is null;
}

public sealed record RegistrySlugAvailability(string? Slug, string? Fqdn, string Status);

public sealed record RegistrySignupRequest(
    string InstitutionName, string Slug, string FirstName, string LastName, string Email, string Phone,
    string Country, string ActivityType, string TokenHash, string? RequestIp, string? UserAgent, string Locale);

public sealed record RegistrySignupCreated(Guid Id, string Fqdn, DateTimeOffset TokenExpiresAt);

public sealed record RegistrySignupConfirmed(Guid Id, string InstitutionName, string Email, bool NewlyConfirmed,
    string FirstName, string LastName, string Phone, string Country, string ActivityType, string Slug, string Locale);

public sealed record RegistrySignupDecision(Guid Id, string Status, string InstitutionName, string FirstName, string Email,
    string? Fqdn, string? RejectionReason, Guid? TenantId, DateTimeOffset DecidedAt, string Locale);

public interface ITrialSignupRegistry
{
    Task<RegistryCall<RegistrySlugAvailability>> CheckSlugAsync(string? slug, CancellationToken ct);
    Task<RegistryCall<RegistrySignupCreated>> CreateAsync(RegistrySignupRequest request, CancellationToken ct);
    Task<RegistryCall<RegistrySignupConfirmed>> ConfirmAsync(string tokenHash, CancellationToken ct);
    Task<RegistryCall<IReadOnlyList<RegistrySignupDecision>>> DecisionsToNotifyAsync(CancellationToken ct);
    Task<RegistryCall<bool>> MarkDecisionNotifiedAsync(Guid signupId, CancellationToken ct);
}

internal sealed class HttpTrialSignupRegistry(IHttpClientFactory httpClientFactory, ILogger<HttpTrialSignupRegistry> logger)
    : ITrialSignupRegistry
{
    private const string Base = "api/v1/tenant-signups";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record ErrorBody(string? Error, string? Detail, Dictionary<string, string>? Errors);

    public Task<RegistryCall<RegistrySlugAvailability>> CheckSlugAsync(string? slug, CancellationToken ct) =>
        SendAsync<RegistrySlugAvailability>(c => c.GetAsync($"{Base}/slug-availability?slug={Uri.EscapeDataString(slug ?? "")}", ct), ct);

    public Task<RegistryCall<RegistrySignupCreated>> CreateAsync(RegistrySignupRequest request, CancellationToken ct) =>
        SendAsync<RegistrySignupCreated>(c => c.PostAsJsonAsync(Base, request, Json, ct), ct);

    public Task<RegistryCall<RegistrySignupConfirmed>> ConfirmAsync(string tokenHash, CancellationToken ct) =>
        SendAsync<RegistrySignupConfirmed>(c => c.PostAsJsonAsync($"{Base}/confirm", new { tokenHash }, Json, ct), ct);

    public async Task<RegistryCall<IReadOnlyList<RegistrySignupDecision>>> DecisionsToNotifyAsync(CancellationToken ct)
    {
        var call = await SendAsync<List<RegistrySignupDecision>>(c => c.GetAsync($"{Base}/decisions-to-notify", ct), ct);
        return new RegistryCall<IReadOnlyList<RegistrySignupDecision>>(call.Value, call.Error, call.Detail, call.Errors);
    }

    public async Task<RegistryCall<bool>> MarkDecisionNotifiedAsync(Guid signupId, CancellationToken ct)
    {
        var call = await SendAsync<object>(c => c.PostAsync($"{Base}/{signupId}/decision-notified", content: null, ct), ct,
            readBody: false);
        return new RegistryCall<bool>(call.IsSuccess, call.Error, call.Detail);
    }

    private async Task<RegistryCall<T>> SendAsync<T>(Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken ct,
        bool readBody = true)
    {
        try
        {
            using var response = await send(httpClientFactory.CreateClient(TenantStoreServiceCollectionExtensions.HttpClientName));
            if (response.IsSuccessStatusCode)
                return new RegistryCall<T>(readBody ? await response.Content.ReadFromJsonAsync<T>(Json, ct) : default, null);

            // A 4xx carries the registry's own code; a 401 (wrong service key) or a 5xx is our outage.
            if ((int)response.StatusCode is >= 400 and < 500 and not (int)HttpStatusCode.Unauthorized)
            {
                var body = await TryReadErrorAsync(response, ct);
                if (body?.Error is { Length: > 0 } code)
                    return new RegistryCall<T>(default, code, body.Detail, body.Errors);
            }

            logger.LogError("Tenant registry answered {Status} to a free-trial call", (int)response.StatusCode);
            return new RegistryCall<T>(default, RegistryCall<T>.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogError(ex, "Tenant registry unreachable for a free-trial call");
            return new RegistryCall<T>(default, RegistryCall<T>.Unavailable);
        }
    }

    private static async Task<ErrorBody?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct); }
        catch (JsonException) { return null; }
    }
}
