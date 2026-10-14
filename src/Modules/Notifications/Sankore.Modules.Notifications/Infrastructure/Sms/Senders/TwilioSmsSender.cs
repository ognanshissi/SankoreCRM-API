namespace Sankore.Modules.Notifications.Infrastructure.Sms.Senders;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

/// <summary>
/// Twilio's Messages resource (<c>POST /2010-04-01/Accounts/{sid}/Messages.json</c>).
///
/// <para>
/// <b>Form-encoded, not JSON.</b> Twilio's REST API takes
/// <c>application/x-www-form-urlencoded</c> and answers JSON — the one place this sender does not
/// look like its Brevo sibling, and the mistake worth naming: posting JSON to it returns a 400
/// whose message is about a missing <c>To</c> parameter, which reads as a bug in the caller's own
/// code rather than as the wrong content type.
/// </para>
///
/// <para>
/// No official SDK. The Twilio helper library would pull in its own HTTP stack, its own retry
/// policy and its own logging, none of which this module wants — the outbox already owns retries
/// and the dead-letter decision, and a library that retries underneath it would multiply a
/// tenant's bill by its own attempt count without any of it appearing in the attempt counter.
/// </para>
/// </summary>
internal sealed class TwilioSmsSender(
    IHttpClientFactory httpClientFactory,
    ISmsCredentials credentials,
    ILogger<TwilioSmsSender> logger) : ISmsSender
{
    public const string HttpClientName = "twilio-sms";

    /// <summary>
    /// A messaging-service SID goes in <c>MessagingServiceSid</c> and a number goes in
    /// <c>From</c>. Twilio rejects the message if the wrong one is used, so the shape of the
    /// configured sender decides which — see <see cref="IsMessagingServiceSid"/>.
    /// </summary>
    private const string MessagingServicePrefix = "MG";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<SmsSendResult> SendAsync(
        SendSmsRequest request, ResolvedSmsProvider provider, CancellationToken ct)
    {
        var authToken = await credentials.GetAsync(request.TenantId, provider, ct);

        if (string.IsNullOrWhiteSpace(authToken))
        {
            throw new InvalidOperationException(
                "Twilio is selected for this tenant's SMS channel but no auth token is in the vault.");
        }

        if (string.IsNullOrWhiteSpace(provider.AccountSid))
        {
            throw new InvalidOperationException(
                "Twilio requires an Account SID; none is configured for this tenant.");
        }

        if (string.IsNullOrWhiteSpace(provider.SenderId))
        {
            throw new InvalidOperationException(
                "Twilio requires a sending number or messaging service; none is configured.");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri("https://api.twilio.com/");

        var form = new List<KeyValuePair<string, string>>
        {
            new("To", request.ToPhone),
            new("Body", request.Body),
        };

        form.Add(IsMessagingServiceSid(provider.SenderId)
            ? new KeyValuePair<string, string>("MessagingServiceSid", provider.SenderId)
            : new KeyValuePair<string, string>("From", provider.SenderId));

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"2010-04-01/Accounts/{provider.AccountSid}/Messages.json")
        {
            Content = new FormUrlEncodedContent(form),
        };

        // HTTP Basic: the Account SID is the user and the Auth Token is the password. Built per
        // request rather than set on the pooled client's DefaultRequestHeaders — that is how one
        // tenant ends up calling with another tenant's token, which M02's biometry client already
        // paid for once.
        message.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{provider.AccountSid}:{authToken}")));

        using var response = await client.SendAsync(message, ct);

        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Twilio's error body carries a numeric code and a URL to its explanation, which is
            // the most useful sentence a dead-letter row can hold for this provider — 21211 is an
            // invalid number, 21610 is an unsubscribed recipient, 20003 is a bad credential, and
            // those three call for three different actions.
            throw new InvalidOperationException(
                $"Twilio rejected the SMS ({(int)response.StatusCode}): "
                + payload[..Math.Min(payload.Length, 500)]);
        }

        var receipt = JsonSerializer.Deserialize<TwilioMessage>(payload, JsonOptions);

        logger.LogInformation(
            "Twilio SMS accepted | MessageId={Id} Sid={Sid} Status={Status} Segments={Segments}",
            request.MessageId, receipt?.Sid, receipt?.Status, receipt?.NumSegments);

        return new SmsSendResult(receipt?.Sid, receipt?.Status);
    }

    /// <summary>
    /// A messaging-service SID is <c>MG</c> plus 32 hex characters; anything else configured is a
    /// number. Shape-based rather than configured as a separate field, because an administrator
    /// who pastes one into a box labelled "sender" has already told us which it is.
    /// </summary>
    private static bool IsMessagingServiceSid(string senderId)
        => senderId.StartsWith(MessagingServicePrefix, StringComparison.Ordinal);

    private sealed record TwilioMessage(
        [property: JsonPropertyName("sid")] string? Sid,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("num_segments")] string? NumSegments);
}
