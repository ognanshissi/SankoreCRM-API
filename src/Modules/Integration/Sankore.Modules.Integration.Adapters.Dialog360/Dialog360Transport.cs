namespace Sankore.Modules.Integration.Adapters.Dialog360;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The one place an HTTP request to 360dialog is built, sent and read back (WA-BE-01).
///
/// <para>
/// <b>The key is per request.</b> 360dialog issues one API key per NUMBER, sent in the
/// <c>D360-API-KEY</c> header; it is set on the <see cref="HttpRequestMessage"/>, never on the
/// pooled client, which every number of every tenant shares.
/// </para>
///
/// <para>
/// <b>Retried with exponential backoff, and only where a retry cannot double-send</b> —
/// criterion 4 of WA-BE-01. A 429 is retried for every verb: the provider refused before doing
/// anything. A 500 or 503 is retried as well: the Cloud API answers those when it did not accept
/// the message, and an accepted message is a 200 carrying its id. A 502 or 504 is NOT retried on a
/// write — those come from a proxy that gave up waiting, which says nothing about whether Meta
/// accepted the message behind it, and a customer receiving the same template twice is the
/// failure this rule exists to avoid. Reads are idempotent and retried on every 5xx.
/// </para>
///
/// <para>
/// <b>Nothing throws</b> except the caller's own cancellation: every failure becomes an
/// <see cref="IntegrationResult"/> through <see cref="Dialog360ErrorClassification"/>, and a body
/// is never logged — a refusal may quote the parameter it refused, which is a customer's name.
/// </para>
/// </summary>
internal sealed class Dialog360Transport(
    IHttpClientFactory httpClientFactory,
    ICallJournal journal,
    TimeProvider clock,
    ILogger<Dialog360Transport> logger)
{
    public const string HttpClientName = "integration-dialog360";

    internal const string ApiKeyHeader = "D360-API-KEY";

    /// <summary>Attempts in all, the first included.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>The first backoff; doubled at each attempt (1 s, 2 s).</summary>
    internal static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);

    /// <summary>The budget of one attempt.</summary>
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The largest response read into memory — a media download is capped separately and lower,
    /// by the caller, against the platform's 10 MB limit.
    /// </summary>
    internal const int MaxResponseBytes = 12 * 1024 * 1024;

    /// <summary>
    /// Sends one request and answers the response body as text, or a classified failure.
    /// </summary>
    public async Task<IntegrationResult<string>> SendAsync(
        Dialog360Binding binding,
        string operation,
        HttpMethod method,
        Uri uri,
        Func<HttpContent?> content,
        CancellationToken ct)
    {
        var context = new CallContext(binding.TenantId, binding.ConnectionId, operation, Endpoint: uri.AbsolutePath);

        return await journal.RecordAsync(
            context,
            async callCt =>
            {
                var result = await SendBytesAsync(binding, context, method, uri, content, callCt);

                return result.IsSuccess
                    ? IntegrationResult.Ok(Encoding.UTF8.GetString(result.Value.Content))
                    : Dialog360Results.Forward<string>(result);
            },
            ct);
    }

    /// <summary>Same, answering the raw bytes and the content type — the media download.</summary>
    public Task<IntegrationResult<Dialog360Response>> SendForBytesAsync(
        Dialog360Binding binding, string operation, Uri uri, CancellationToken ct)
    {
        var context = new CallContext(binding.TenantId, binding.ConnectionId, operation, Endpoint: uri.AbsolutePath);

        return journal.RecordAsync(
            context,
            callCt => SendBytesAsync(binding, context, HttpMethod.Get, uri, () => null, callCt),
            ct);
    }

    private async Task<IntegrationResult<Dialog360Response>> SendBytesAsync(
        Dialog360Binding binding,
        CallContext context,
        HttpMethod method,
        Uri uri,
        Func<HttpContent?> content,
        CancellationToken ct)
    {
        var isRead = method == HttpMethod.Get;
        var backoff = FirstBackoff;

        for (var attempt = 1; ; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(AttemptTimeout);

            HttpResponseMessage response;

            try
            {
                // A FRESH request per attempt: HttpClient refuses to send one instance twice, and
                // the content factory rebuilds a multipart body the first attempt consumed.
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.TryAddWithoutValidation(ApiKeyHeader, binding.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation(CallJournal.CorrelationHeader, context.Correlation);
                request.Content = content();

                response = await httpClientFactory.CreateClient(HttpClientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own budget ran out. On a write this is the ambiguous case — the provider may
                // have accepted — so it is reported and never retried here.
                if (isRead && attempt < MaxAttempts && await WaitAsync(backoff, ct))
                {
                    backoff *= 2;
                    continue;
                }

                return IntegrationResult.Transient<Dialog360Response>(
                    IntegrationErrors.Timeout,
                    $"{context.Operation}: 360dialog did not answer within {AttemptTimeout.TotalSeconds:0} s.");
            }
            catch (HttpRequestException ex)
            {
                // Nothing left this process, or the connection was refused: safe to retry for
                // every verb, because the provider never saw a complete request.
                if (attempt < MaxAttempts && ex.StatusCode is null && await WaitAsync(backoff, ct))
                {
                    backoff *= 2;
                    continue;
                }

                logger.LogWarning(
                    "360dialog unreachable | Operation={Operation} Connection={ConnectionId} Reason={Reason}",
                    context.Operation, binding.ConnectionId, ex.GetType().Name);

                return IntegrationResult.Transient<Dialog360Response>(
                    IntegrationErrors.Unavailable, $"{context.Operation}: 360dialog is unreachable.");
            }

            using (response)
            {
                context.Probe.HttpStatus = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    var bytes = await ReadBoundedAsync(response, ct);

                    if (bytes is null)
                    {
                        return IntegrationResult.Functional<Dialog360Response>(
                            IntegrationErrors.MessagingMediaRefused,
                            $"{context.Operation}: the answer exceeds {MaxResponseBytes / (1024 * 1024)} MB.");
                    }

                    return IntegrationResult.Ok(new Dialog360Response(
                        bytes,
                        response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
                        response.Content.Headers.ContentDisposition?.FileNameStar
                            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')));
                }

                var body = await ReadErrorBodyAsync(response, ct);

                if (attempt < MaxAttempts
                    && IsRetryable(response.StatusCode, isRead)
                    && await WaitAsync(RetryAfter(response) ?? backoff, ct))
                {
                    logger.LogInformation(
                        "360dialog answered {Status} on {Operation}; attempt {Attempt} of {Max}.",
                        (int)response.StatusCode, context.Operation, attempt + 1, MaxAttempts);

                    backoff *= 2;
                    continue;
                }

                return Dialog360Results.Forward<Dialog360Response>(
                    Dialog360ErrorClassification.Classify(response.StatusCode, body, context.Operation));
            }
        }
    }

    /// <summary>See the class remarks: what may be sent again without risking a second message.</summary>
    internal static bool IsRetryable(HttpStatusCode status, bool isRead) => (int)status switch
    {
        429 => true,
        500 or 503 => true,
        502 or 504 => isRead,
        _ => false,
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var delta = response.Headers.RetryAfter?.Delta;

        // Honoured, but bounded: a provider asking for a minute is asking the caller to give up,
        // and M13 then records the message as failed for the agent to send again.
        return delta is { } d && d > TimeSpan.Zero && d <= TimeSpan.FromSeconds(10) ? d : null;
    }

    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, clock, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.ToArray();
    }

    private static async Task<string?> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var bytes = await ReadBoundedAsync(response, ct);

            // An error body is a few hundred bytes; a proxy's HTML page is not worth classifying.
            return bytes is null || bytes.Length > 64 * 1024 ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>JSON content in the Cloud API's spelling: snake_case keys are written by hand.</summary>
    internal static HttpContent Json(object body)
        => new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}

/// <summary>A successful answer, as bytes.</summary>
internal sealed record Dialog360Response(byte[] Content, string ContentType, string? FileName);

/// <summary>
/// One connection, ready to call: where, and with which key. Built per call by the adapter from
/// the connection it was handed and the vault — never cached across calls, so a key rotated in the
/// vault is used from the next message on.
/// </summary>
internal sealed record Dialog360Binding(Guid TenantId, Guid ConnectionId, Uri BaseUrl, string ApiKey)
{
    /// <summary>Never print the key — the record's generated ToString would.</summary>
    public override string ToString() => $"Dialog360Binding {{ ConnectionId = {ConnectionId} }}";
}

/// <summary>Carries a failure across result types without losing its family.</summary>
internal static class Dialog360Results
{
    public static IntegrationResult<T> Forward<T>(IntegrationResult source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.IsSuccess)
            throw new InvalidOperationException("Only a failure can be forwarded.");

        // The family is the whole point of IntegrationResult: dropping it in a conversion would
        // turn a throttle into a refusal, and a refusal into a retry.
        return source.Family switch
        {
            ErrorFamily.Functional => IntegrationResult.Functional<T>(source.Code!, source.Detail),
            ErrorFamily.Technical => IntegrationResult.Technical<T>(source.Code!, source.Detail),
            _ => IntegrationResult.Transient<T>(source.Code!, source.Detail),
        };
    }

    public static IntegrationResult Forward(IntegrationResult source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.IsSuccess)
            throw new InvalidOperationException("Only a failure can be forwarded.");

        return source.Family switch
        {
            ErrorFamily.Functional => IntegrationResult.Functional(source.Code!, source.Detail),
            ErrorFamily.Technical => IntegrationResult.Technical(source.Code!, source.Detail),
            _ => IntegrationResult.Transient(source.Code!, source.Detail),
        };
    }
}
