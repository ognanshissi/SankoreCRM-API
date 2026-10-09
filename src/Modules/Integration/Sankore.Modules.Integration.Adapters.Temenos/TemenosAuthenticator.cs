namespace Sankore.Modules.Integration.Adapters.Temenos;

using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Obtains the bearer token a call travels with — OAuth 2.0 client credentials, or the static
/// token the installation issued (INT-12, criterion 2).
///
/// <para>
/// <b>The credential is read from the vault on the path that needs it and never from the
/// connection's settings.</b> <c>TemenosSettings</c> carries coordinates — base URL, token
/// endpoint, client id, scope, company — and a vault REFERENCE. The value lives under
/// <c>IntegrationSecrets.CredentialKey(tenantId, connectionId)</c> and reaches no column, no
/// endpoint, no event and no audit row. Keyed per connection and not per tenant because an IMF
/// has a core banking system and two insurers at once, and a tenant-wide key would make saving
/// the second credential destroy the first.
/// </para>
///
/// <para>
/// <b>Both modes renew.</b> For client credentials that is obvious: the cache drops the token
/// before it expires and this class mints another. For a static token it is less so, and it
/// matters more — the only way a rotated token is ever picked up is that a 401 invalidates the
/// cache and the next attempt re-READS the vault. Without that, an administrator who pastes a new
/// token into INT-03's secret endpoint would see nothing change until the process restarted.
/// </para>
/// </summary>
internal sealed class TemenosAuthenticator(
    IHttpClientFactory httpClientFactory,
    ISecretsModule secrets,
    TemenosTokenCache cache,
    TimeProvider clock,
    IOptions<TemenosAdapterOptions> options,
    ILogger<TemenosAuthenticator> logger)
{
    /// <summary>
    /// The token for this connection, cached and single-flighted. <paramref name="forceRenew"/>
    /// comes from the transport after a 401.
    /// </summary>
    public Task<IntegrationResult<string>> GetTokenAsync(
        TemenosBinding binding, bool forceRenew, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return cache.GetAsync(
            binding.Scope,
            callCt => MintAsync(binding, callCt),
            forceRenew,
            ct);
    }

    /// <summary>Drops the cached token, so the next call authenticates from scratch.</summary>
    public void Invalidate(TemenosBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cache.Invalidate(binding.Scope);
    }

    private async Task<IntegrationResult<TemenosToken>> MintAsync(
        TemenosBinding binding, CancellationToken ct)
    {
        // Read before a single packet leaves: a connection with no stored credential must not
        // produce an unauthenticated call, which would come back as a 401 and read like refused
        // credentials rather than like absent ones. The two send an administrator to different
        // screens.
        var credential = await secrets.GetValueAsync(
            IntegrationSecrets.CredentialKey(binding.TenantId, binding.ConnectionId), ct);

        if (string.IsNullOrWhiteSpace(credential))
        {
            logger.LogWarning(
                "No Temenos credential stored for connection {ConnectionId}; no call will be made.",
                binding.ConnectionId);

            return IntegrationResult.Technical<TemenosToken>(
                IntegrationErrors.CredentialMissing,
                "No credential is stored in the vault for this Temenos connection.");
        }

        return binding.Settings.AuthMode == TemenosAuthMode.StaticToken
            ? StaticToken(credential)
            : await RequestClientCredentialsTokenAsync(binding, credential, ct);
    }

    /// <summary>
    /// The vault value IS the bearer token.
    ///
    /// <para>
    /// It is still handed to the cache with the fallback lifetime rather than cached for ever, so
    /// a rotation is picked up within one lifetime even if nothing ever answers 401 — a long-lived
    /// token is usually revoked rather than rejected, and a revoked token that is never re-read is
    /// an outage nobody can explain.
    /// </para>
    /// </summary>
    private static IntegrationResult<TemenosToken> StaticToken(string credential)
        => IntegrationResult.Ok(new TemenosToken(credential.Trim(), ExpiresAt: null));

    private async Task<IntegrationResult<TemenosToken>> RequestClientCredentialsTokenAsync(
        TemenosBinding binding, string clientSecret, CancellationToken ct)
    {
        var settings = binding.Settings;

        if (string.IsNullOrWhiteSpace(settings.TokenEndpoint) || string.IsNullOrWhiteSpace(settings.OAuthClientId))
        {
            // Technical and not transient: no amount of retrying fills in a token endpoint.
            return IntegrationResult.Technical<TemenosToken>(
                IntegrationErrors.SettingsInvalid,
                "OAuth client credentials need both TokenEndpoint and OAuthClientId on the connection.");
        }

        if (!Uri.TryCreate(settings.TokenEndpoint, UriKind.Absolute, out var endpoint))
        {
            return IntegrationResult.Technical<TemenosToken>(
                IntegrationErrors.SettingsInvalid,
                "The connection's TokenEndpoint is not an absolute URL.");
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", settings.OAuthClientId!),
            new("client_secret", clientSecret),
        };

        if (!string.IsNullOrWhiteSpace(settings.OAuthScope))
            form.Add(new KeyValuePair<string, string>("scope", settings.OAuthScope!));

        // Its own budget, shorter than a business call's: an authorisation server that is slow is
        // unavailable, and spending the whole call budget here leaves nothing for the operation
        // the caller actually asked for. Linked, so the caller's cancellation still wins.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(options.Value.TokenTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var client = httpClientFactory.CreateClient(TemenosTransport.HttpClientName);

        try
        {
            using var response = await client.SendAsync(request, attempt.Token);
            var body = await response.Content.ReadAsStringAsync(attempt.Token);

            if (!response.IsSuccessStatusCode)
            {
                // The body is NOT quoted: an authorisation server echoes the client id, and some
                // echo the submitted secret back in a validation message.
                logger.LogWarning(
                    "Temenos token endpoint answered HTTP {Status} for connection {ConnectionId}.",
                    (int)response.StatusCode, binding.ConnectionId);

                return IntegrationResult.Technical<TemenosToken>(
                    IntegrationErrors.AuthenticationRefused,
                    $"The Temenos token endpoint answered HTTP {(int)response.StatusCode}.");
            }

            var token = JsonSerializer.Deserialize<TemenosOAuthToken>(body, TemenosJson.Options);

            if (string.IsNullOrWhiteSpace(token?.AccessToken))
            {
                return IntegrationResult.Technical<TemenosToken>(
                    IntegrationErrors.AuthenticationRefused,
                    "The Temenos token endpoint answered 200 without an access_token.");
            }

            // expires_in is optional in RFC 6749. Null here means "the cache decides", which is
            // the fallback lifetime and never "for ever".
            var expiresAt = token.ExpiresIn is > 0
                ? clock.GetUtcNow().AddSeconds(token.ExpiresIn.Value)
                : (DateTimeOffset?)null;

            return IntegrationResult.Ok(new TemenosToken(token.AccessToken!.Trim(), expiresAt));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return IntegrationResult.Transient<TemenosToken>(
                IntegrationErrors.Timeout,
                $"No answer from the Temenos token endpoint within {options.Value.TokenTimeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException ex)
        {
            // Unreachable is an outage, never refused credentials: classifying it as Technical
            // would stop the dispatcher retrying a command that will go through in a minute.
            return IntegrationResult.Transient<TemenosToken>(
                IntegrationErrors.Unavailable,
                $"The Temenos token endpoint could not be reached ({ex.GetType().Name}).");
        }
        catch (JsonException)
        {
            return IntegrationResult.Functional<TemenosToken>(
                IntegrationErrors.UnexpectedResponse,
                "The Temenos token endpoint answered a body that is not an OAuth token response.");
        }
    }
}
