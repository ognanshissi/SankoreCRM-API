namespace Sankore.Modules.Integration.Features.Webhooks;

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Infrastructure.Egress;
using Sankore.Shared.Kernel;

/// <summary>
/// The signing secret of a subscription, in the vault (scope <c>integration</c>, the subscription's
/// id). Shown once — at creation and at each rotation — and never returned again.
///
/// <para>
/// A rotation keeps the previous secret for <see cref="RotationOverlap"/> and every delivery in
/// that window carries BOTH signatures, so the partner can switch keys whenever it likes inside
/// the week rather than at the instant the administrator clicked.
/// </para>
/// </summary>
internal sealed class WebhookSecrets(ISecretsModule vault, TimeProvider clock)
{
    public const string Scope = "integration";
    public const string CurrentName = "webhook-signing";
    public const string PreviousName = "webhook-signing-old";
    public static readonly TimeSpan RotationOverlap = TimeSpan.FromDays(7);

    /// <summary><c>whsec_</c> + 32 random bytes, base64url. The whole string is the HMAC key.</summary>
    public static string Generate()
        => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<string> CreateAsync(Guid tenantId, Guid subscriptionId, CancellationToken ct)
    {
        var secret = Generate();
        await vault.SetAsync(Key(tenantId, subscriptionId, CurrentName), secret, ct: ct);
        return secret;
    }

    /// <returns>The new secret, and until when the previous one still signs.</returns>
    public async Task<(string Secret, DateTimeOffset PreviousValidUntil)> RotateAsync(
        Guid tenantId, Guid subscriptionId, CancellationToken ct)
    {
        var until = clock.GetUtcNow() + RotationOverlap;
        var current = await vault.GetValueAsync(Key(tenantId, subscriptionId, CurrentName), ct);
        if (current is not null)
            await vault.SetAsync(Key(tenantId, subscriptionId, PreviousName), current, until, ct);

        var secret = Generate();
        await vault.SetAsync(Key(tenantId, subscriptionId, CurrentName), secret, ct: ct);
        return (secret, until);
    }

    /// <summary>
    /// The secrets a delivery is signed with: the current one, then the previous one while its
    /// window is open. The vault keeps an expired value; the expiry is checked here.
    /// </summary>
    public async Task<IReadOnlyList<string>> SigningKeysAsync(Guid tenantId, Guid subscriptionId, CancellationToken ct)
    {
        var keys = new List<string>(2);
        var current = await vault.GetValueAsync(Key(tenantId, subscriptionId, CurrentName), ct);
        if (current is not null) keys.Add(current);

        var previousKey = Key(tenantId, subscriptionId, PreviousName);
        var hint = await vault.GetHintAsync(previousKey, ct);
        if (hint?.ExpiresAt is { } expires && expires > clock.GetUtcNow())
        {
            var previous = await vault.GetValueAsync(previousKey, ct);
            if (previous is not null) keys.Add(previous);
        }

        return keys;
    }

    public Task DeleteAsync(Guid tenantId, Guid subscriptionId, CancellationToken ct)
        => vault.DeleteAllAsync(tenantId, Scope, subscriptionId, ct);

    private static SecretKey Key(Guid tenantId, Guid subscriptionId, string name) => new(tenantId, Scope, subscriptionId, name);
}

/// <summary>
/// The body of every delivery and its signature header. One shape for every event:
/// <code>{ "id", "type", "version", "occurredAt", "test"?, "data": { … } }</code>
/// <c>id</c> is the event's: the same event retried keeps it, so it is the partner's idempotency key.
///
/// <para>
/// <c>X-Sankore-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex&gt;[,v1=&lt;hex&gt;]</c> — HMAC-SHA256 of
/// <c>"{t}.{body}"</c> keyed by the secret string. The same format the inbound lead-source webhook
/// verifies, so a partner who does both writes one verifier.
/// </para>
/// </summary>
internal static class WebhookPayload
{
    public const string SignatureHeader = "X-Sankore-Signature";
    public const string EventHeader = "X-Sankore-Event";
    public const string DeliveryHeader = "X-Sankore-Delivery";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Body(WebhookEventDefinition definition, IIntegrationEvent @event, bool test = false)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["id"] = @event.EventId,
            ["type"] = definition.Code,
            ["version"] = definition.Version,
            ["occurredAt"] = @event.OccurredAt,
        };
        if (test) envelope["test"] = true;
        envelope["data"] = definition.Data(@event);

        return JsonSerializer.Serialize(envelope, Json);
    }

    public static string Signature(string body, long timestamp, IReadOnlyList<string> secrets)
    {
        var signed = Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{body}");
        var parts = secrets.Select(s => "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(s), signed)));
        return $"t={timestamp.ToString(CultureInfo.InvariantCulture)}," + string.Join(",", parts);
    }
}

/// <summary>
/// One POST to a subscriber, through the platform's guarded outbound client — HTTPS only, no
/// internal address, allowed ports only. Never retries: retrying is the delivery queue's (lot 3),
/// which decides when and how often.
/// </summary>
internal sealed class WebhookSender(IHttpClientFactory httpClientFactory, TimeProvider clock, ILogger<WebhookSender> logger)
{
    public const string HttpClientName = "WebhookDelivery";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<WebhookAttempt> SendAsync(
        string url, string eventCode, Guid deliveryId, string body, IReadOnlyList<string> secrets, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(WebhookPayload.EventHeader, eventCode);
        request.Headers.TryAddWithoutValidation(WebhookPayload.DeliveryHeader, deliveryId.ToString());
        request.Headers.TryAddWithoutValidation(WebhookPayload.SignatureHeader,
            WebhookPayload.Signature(body, clock.GetUtcNow().ToUnixTimeSeconds(), secrets));
        request.Headers.UserAgent.ParseAdd("Sankore-Webhooks/1.0");

        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            var status = (int)response.StatusCode;
            return new WebhookAttempt(
                status, response.IsSuccessStatusCode, Elapsed(started),
                response.IsSuccessStatusCode ? null : $"HTTP_{status}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebhookAttempt(null, false, Elapsed(started), "TIMEOUT");
        }
        catch (HttpRequestException ex) when (ex.InnerException is DomainException { Message: SafeOutboundHttp.RefusedCode })
        {
            return new WebhookAttempt(null, false, Elapsed(started), "ADDRESS_REFUSED");
        }
        catch (DomainException ex) when (ex.Message == SafeOutboundHttp.RefusedCode)
        {
            // A redirect to another host or to http, refused by SafeRedirectHandler.
            return new WebhookAttempt(null, false, Elapsed(started), "REDIRECT_REFUSED");
        }
        catch (HttpRequestException ex)
        {
            // The URL is logged, never the body: it carries the tenant's events.
            logger.LogInformation(ex, "Webhook delivery {DeliveryId} to {Url} could not connect", deliveryId, url);
            return new WebhookAttempt(null, false, Elapsed(started), ex.StatusCode is HttpStatusCode code ? $"HTTP_{(int)code}" : "UNREACHABLE");
        }
    }

    private int Elapsed(long started) => (int)clock.GetElapsedTime(started).TotalMilliseconds;
}

/// <param name="FailureCode">Null on success; else <c>HTTP_&lt;status&gt;</c>, <c>TIMEOUT</c>,
/// <c>UNREACHABLE</c>, <c>ADDRESS_REFUSED</c> or <c>REDIRECT_REFUSED</c>.</param>
public sealed record WebhookAttempt(int? StatusCode, bool Succeeded, int DurationMs, string? FailureCode);
