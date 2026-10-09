namespace Sankore.Modules.Integration.Adapters.Temenos;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.Infrastructure.Resilience;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The one place an HTTP request to Transact is built, sent, and read back.
///
/// <para>
/// Modelled on M02's <c>HttpBiometryClient</c>, and for the same four reasons:
/// </para>
///
/// <list type="number">
/// <item><b>The token is per call and per tenant.</b> It is set on the
///   <see cref="HttpRequestMessage"/>, never on the pooled client's
///   <c>DefaultRequestHeaders</c> — that is precisely how one tenant ends up calling with
///   another tenant's token, and the pool is shared by every connection of every tenant.</item>
/// <item><b>The correlation id is per call</b> and is the SAME id the call journal records, so a
///   support ticket joins our row to the installation's own log.</item>
/// <item><b>The budget is a linked cancellation token</b> and not <c>HttpClient.Timeout</c>,
///   which is what lets our own expiry (a transient result) be told apart from the caller giving
///   up (rethrown) — <c>HttpClient.Timeout</c> reports both as the same exception.</item>
/// <item><b>Nothing throws.</b> Every failure mode becomes an <see cref="IntegrationResult"/>
///   through <see cref="TemenosErrorClassifier"/>. The one exception that leaves is the caller's
///   own cancellation, which is not an answer from Temenos.</item>
/// </list>
///
/// <para>
/// <b>One retry, and only on a 401.</b> It is not a resilience policy: it is the renewal half of
/// INT-12's criterion 2. The installation has just told us the token we hold is no longer
/// accepted, which is the one piece of information our expiry arithmetic cannot have — a
/// revocation, a rotated signing key, a clock further out than the margin. So the cached token is
/// dropped, a fresh one is minted, and the request is sent once more. A second 401 is believed
/// and reported as <c>AuthenticationRefused</c>. Everything else is passed to the classifier
/// untouched: retrying a refusal belongs to the dispatcher and to the command's attempt budget,
/// never to a loop inside an adapter, which no attempt budget can see.
/// </para>
/// </summary>
internal sealed class TemenosTransport(
    IHttpClientFactory httpClientFactory,
    TemenosAuthenticator authenticator,
    IntegrationResiliencePipelineProvider pipelines,
    IOptions<TemenosAdapterOptions> options,
    ILogger<TemenosTransport> logger)
{
    /// <summary>
    /// Named client, so the host owns the handler chain, the proxy and the connection pool.
    ///
    /// <para>
    /// <b>No <c>SsrfSafeHandler</c> on it, deliberately — do not add one.</b> M13's handler
    /// refuses every RFC 1918 address, and a Temenos Transact installation is frequently
    /// on-premise at <c>10.x</c> or <c>192.168.x</c>: that is the ordinary case this adapter
    /// exists to serve, not an attack. The threat the handler defends against is a URL supplied by
    /// a caller; a connection's base URL is configured by an administrator of the deployment
    /// through INT-03. It is opt-in per <c>HttpClient</c>, so the correct action is simply never
    /// to call it.
    /// </para>
    /// </summary>
    public const string HttpClientName = "integration-temenos";

    /// <summary>
    /// Mints a token and nothing else, so the activation screen can tell "these credentials are
    /// refused" from "the installation is down".
    ///
    /// <para>
    /// Always renewed, never served from the cache: the question being asked is whether the stored
    /// credential works at this moment, and answering it from a token minted ten minutes ago would
    /// let a connection be activated on a credential that has since been revoked.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<string>> ProveCredentialsAsync(
        TemenosBinding binding, CancellationToken ct)
        => authenticator.GetTokenAsync(binding, forceRenew: true, ct);

    /// <summary>
    /// Sends one request and returns the parsed envelope, or a classified failure.
    /// </summary>
    /// <param name="context">
    /// The journal's context for the surrounding operation. Read for the correlation id, written
    /// to for the HTTP status — the status is the one fact the journal's own row cannot get from
    /// an <see cref="IntegrationResult"/>, which carries a family and a code and no status.
    /// </param>
    /// <param name="idempotencyKey">
    /// Set on writes. Sent as a header on the chance the installation honours it; the duplicate
    /// protection this adapter actually relies on is the pre-create search of INT-12.
    /// </param>
    public async Task<IntegrationResult<TemenosEnvelope<T>>> SendAsync<T>(
        TemenosBinding binding,
        CallContext context,
        HttpMethod method,
        Uri uri,
        object? body,
        IdempotencyKey? idempotencyKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(uri);

        var operation = context.Operation;
        var budget = BudgetFor(binding);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(budget);

        // Two passes at most: the second only ever happens after a 401, with a freshly minted
        // token. See the class remarks for why this is renewal and not a retry policy.
        for (var pass = 0; pass < 2; pass++)
        {
            var token = await authenticator.GetTokenAsync(binding, forceRenew: pass > 0, attempt.Token);
            if (token.IsFailure)
                return IntegrationResultForwarding.Forward<TemenosEnvelope<T>>(token);

            HttpResponseMessage response;

            try
            {
                // Through INT-09's per-connection pipeline, and this is what makes that story real
                // rather than declared: until the only live adapter ran inside it, no circuit ever
                // opened, the module's health check reported an unknown state for every
                // connection, and an installation that had stopped answering was called again on
                // every single command.
                //
                // Read pipeline for a GET, write pipeline otherwise — INT-09 asks for the short
                // retry on direct reads ONLY. Retrying a write here would double-send it: the
                // dispatcher already owns that budget, and a POST whose answer was lost is exactly
                // the ambiguous timeout INT-12's pre-create search exists to settle.
                //
                // The linked token above stays the outer bound: a pipeline timeout is per attempt,
                // this budget is for the whole call.
                var pipeline = method == HttpMethod.Get
                    ? pipelines.GetReadPipeline(binding.Connection)
                    : pipelines.GetWritePipeline(binding.Connection);

                response = await pipeline.ExecuteAsync(
                    async attemptToken =>
                    {
                        // A FRESH HttpRequestMessage per attempt. HttpClient refuses to send the
                        // same instance twice, so a request built once outside the pipeline makes
                        // the first retry throw InvalidOperationException instead of retrying —
                        // the retry looks configured and is dead. Rebuilding also re-reads the
                        // token, which is what a 401 pass needs anyway.
                        using var request = BuildRequest(
                            binding, context, method, uri, body, idempotencyKey, token.Value);

                        return await httpClientFactory.CreateClient(HttpClientName)
                            .SendAsync(request, attemptToken);
                    },
                    attempt.Token);
            }
            catch (BrokenCircuitException)
            {
                // Polly signals an open circuit by throwing, and the module's contract is that a
                // port method never throws. Caught FIRST, before the cancellation handler: a
                // broken circuit is not a timeout, and reporting it as one would hide the reason
                // the call was never attempted from the operator reading the rejection queue.
                logger.LogWarning(
                    "Temenos circuit open | Operation={Operation} Connection={ConnectionId} "
                    + "Correlation={Correlation}",
                    operation, binding.ConnectionId, context.Correlation);

                return Fail<T>(TemenosErrorClassifier.CircuitOpen(operation, binding.ConnectionId));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail<T>(TemenosErrorClassifier.TimedOut(operation, budget));
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(
                    ex,
                    "Temenos unreachable | Operation={Operation} Connection={ConnectionId} "
                    + "Correlation={Correlation}",
                    operation, binding.ConnectionId, context.Correlation);

                return Fail<T>(TemenosErrorClassifier.Unreachable(operation, ex.GetType().Name));
            }

            using (response)
            {
                // Written whatever happens next, including on the 401 that leads to a second
                // pass: a journal that recorded only the last status would hide the renewal.
                context.Probe.HttpStatus = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.Unauthorized && pass == 0)
                {
                    logger.LogInformation(
                        "Temenos answered 401 on {Operation}; renewing the token for connection "
                        + "{ConnectionId} and sending once more.",
                        operation, binding.ConnectionId);

                    authenticator.Invalidate(binding);
                    continue;
                }

                return await ReadAsync<T>(response, operation, budget, attempt.Token, ct);
            }
        }

        // Unreachable in practice: the loop either returns or continues exactly once. Kept
        // explicit rather than left to the compiler, so the shape of the renewal is obvious.
        return Fail<T>(IntegrationResult.Technical(
            IntegrationErrors.AuthenticationRefused,
            $"{operation}: Temenos refused the request twice, with a freshly minted token."));
    }

    /// <summary>
    /// Reads the body, classifies it, and parses it. In that order: a refusal carried on a 2xx —
    /// which IRIS does — must not be read as a success merely because the transport was happy.
    /// </summary>
    private static async Task<IntegrationResult<TemenosEnvelope<T>>> ReadAsync<T>(
        HttpResponseMessage response,
        string operation,
        TimeSpan budget,
        CancellationToken attemptCt,
        CancellationToken callerCt)
    {
        string payload;

        try
        {
            payload = await response.Content.ReadAsStringAsync(attemptCt);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            return Fail<T>(TemenosErrorClassifier.TimedOut(operation, budget));
        }
        catch (HttpRequestException ex)
        {
            // Reading the body can fail on its own — a reset mid-stream, or the response cap in
            // TemenosAdapterOptions.MaxResponseBytes being exceeded by a gateway's HTML page.
            return Fail<T>(TemenosErrorClassifier.Unreachable(operation, ex.GetType().Name));
        }

        TemenosEnvelope<T>? envelope = null;
        var unparseable = false;

        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                envelope = JsonSerializer.Deserialize<TemenosEnvelope<T>>(payload, TemenosJson.Options);
            }
            catch (JsonException)
            {
                // Not logged with the body: a gateway's error page is up to a megabyte, and a
                // genuine T24 refusal quotes the rejected value, which is personal data.
                unparseable = true;
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            // The envelope is used only for its error block here; an unparseable body simply
            // means the classifier works from the status alone, which is still correct.
            return Fail<T>(TemenosErrorClassifier.Classify(response.StatusCode, envelope?.Error, operation));
        }

        if (unparseable)
            return Fail<T>(TemenosErrorClassifier.Unexpected(operation, "the body is not JSON"));

        if (envelope is null)
        {
            // A 2xx with no body at all. Legitimate for a PUT that acknowledges nothing, so an
            // empty envelope is handed back and the caller decides whether it needed a record.
            return IntegrationResult.Ok(new TemenosEnvelope<T>(Header: null, Body: null, Error: null));
        }

        return envelope.HasError
            ? Fail<T>(TemenosErrorClassifier.Classify(response.StatusCode, envelope.Error, operation))
            : IntegrationResult.Ok(envelope);
    }

    /// <summary>
    /// Builds the request message. Everything per-call lives here: the bearer token, the
    /// correlation id, the company, and the idempotency key.
    /// </summary>
    private static HttpRequestMessage BuildRequest(
        TemenosBinding binding,
        CallContext context,
        HttpMethod method,
        Uri uri,
        object? body,
        IdempotencyKey? idempotencyKey,
        string token)
    {
        var request = new HttpRequestMessage(method, uri);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The journal's own id, echoed on the wire. CallContext.Correlation is never null, so
        // every outbound call carries one and every row can be joined to it.
        request.Headers.TryAddWithoutValidation(CallJournal.CorrelationHeader, context.Correlation);

        if (!string.IsNullOrWhiteSpace(binding.Settings.CompanyId))
            request.Headers.TryAddWithoutValidation(TemenosHeaders.CompanyId, binding.Settings.CompanyId);

        if (idempotencyKey is { } key && !string.IsNullOrWhiteSpace(key.Value))
            request.Headers.TryAddWithoutValidation(TemenosHeaders.IdempotencyKey, key.Value);

        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: TemenosJson.Options);

        return request;
    }

    /// <summary>
    /// The per-call budget: the connection's own <c>TimeoutSeconds</c> when it names one, the
    /// deployment's default otherwise. Per connection because a Transact behind a VPN at an IMF
    /// is a different animal from one in the same data centre.
    /// </summary>
    private TimeSpan BudgetFor(TemenosBinding binding)
        => binding.Settings.TimeoutSeconds > 0
            ? TimeSpan.FromSeconds(binding.Settings.TimeoutSeconds)
            : options.Value.DefaultTimeout;

    private static IntegrationResult<TemenosEnvelope<T>> Fail<T>(IntegrationResult failure)
        => IntegrationResultForwarding.Forward<TemenosEnvelope<T>>(failure);
}
