using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Tenants;

namespace Sankore.Api.Features.TrialSignups;

/// <summary>
/// What is left of the free trial's settings on this side, bound from <c>TrialSignup</c>: the rate of
/// the public form. Everything else — the confirmation page, the captcha's secret, the team told of
/// each request, the address and the length of the trial — is the REGISTRY's <c>TrialSignup</c>
/// section (Sankore.Admin), which decides the form since 2026-11-12.
/// </summary>
public sealed class TrialSignupOptions
{
    public const string SectionName = "TrialSignup";

    /// <summary>Requests a single IP may send per hour.</summary>
    public int RequestsPerHourPerIp { get; set; } = 5;
}

// ── The registry's routes (Sankore.Admin, service key) ──────────────────────────────────────────

public sealed record RegistryCall<T>(T? Value, string? Error, string? Detail = null,
    IReadOnlyDictionary<string, string>? Errors = null)
{
    public const string Unavailable = "TRIAL_REGISTRY_UNAVAILABLE";
    public bool IsSuccess => Error is null;
}

/// <summary>An e-mail the registry asks this API to queue (<c>TrialMail</c> on its side).</summary>
public sealed record RegistryMail(string TemplateKey, string To, string? Name, string Locale,
    Dictionary<string, string>? Data, string IdempotencyKey);

/// <summary>
/// The registry's answer to a public route (<c>TrialPublicAnswer</c> on its side): the status and body
/// the website gets, relayed untouched, and the e-mails to queue.
/// </summary>
public sealed record RegistryPublicAnswer(int StatusCode, JsonElement Body, List<RegistryMail>? Mails);

public sealed record RegistrySignupDecision(Guid Id, string Status, string InstitutionName, string FirstName, string Email,
    string? Fqdn, string? RejectionReason, Guid? TenantId, DateTimeOffset DecidedAt, string Locale);

public interface ITrialSignupRegistry
{
    Task<RegistryCall<RegistryPublicAnswer>> CheckSlugForPublicAsync(string? slug, CancellationToken ct);
    Task<RegistryCall<RegistryPublicAnswer>> SubmitForPublicAsync(JsonElement form, string? clientIp, string? userAgent,
        CancellationToken ct);
    Task<RegistryCall<RegistryPublicAnswer>> ConfirmForPublicAsync(string? token, CancellationToken ct);
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

    public Task<RegistryCall<RegistryPublicAnswer>> CheckSlugForPublicAsync(string? slug, CancellationToken ct) =>
        SendAsync<RegistryPublicAnswer>(c => c.GetAsync(
            $"{Base}/public/slug-availability?slug={Uri.EscapeDataString(slug ?? "")}", ct), ct);

    public Task<RegistryCall<RegistryPublicAnswer>> SubmitForPublicAsync(JsonElement form, string? clientIp,
        string? userAgent, CancellationToken ct) =>
        SendAsync<RegistryPublicAnswer>(c => c.PostAsJsonAsync($"{Base}/public/submit",
            new { form, clientIp, userAgent }, Json, ct), ct);

    public Task<RegistryCall<RegistryPublicAnswer>> ConfirmForPublicAsync(string? token, CancellationToken ct) =>
        SendAsync<RegistryPublicAnswer>(c => c.PostAsJsonAsync($"{Base}/public/confirm", new { token }, Json, ct), ct);

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

    // A public route always answers 200 with the website's answer inside; any other status here is
    // an outage of the relay (wrong service key, registry down), never something to show the visitor.
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
