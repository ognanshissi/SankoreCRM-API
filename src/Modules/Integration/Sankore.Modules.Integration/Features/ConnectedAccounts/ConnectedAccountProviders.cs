namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;

/// <summary>What a token endpoint answered.</summary>
/// <param name="RefreshToken">Null when the provider did not issue a new one — the old one stays valid.</param>
internal sealed record ProviderTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string Scopes);

/// <summary>
/// What a consent asks for beyond the agendas: the mailbox (connected mailbox, lot 1), and the
/// address to pre-fill on the provider's screen when an existing account is reconnected.
/// </summary>
internal sealed record ProviderConsent(bool IncludeMail = false, string? LoginHint = null)
{
    public static readonly ProviderConsent Default = new();
}

/// <summary>Who the account is, as the provider says.</summary>
internal sealed record ProviderProfile(string ExternalId, string Email, string? DisplayName);

/// <summary>
/// The provider refused. <see cref="Revoked"/> separates « the user must consent again » (an
/// <c>invalid_grant</c>: revoked, expired, password changed) from a provider that is merely
/// unavailable, which must not send anybody back through the consent screen.
/// </summary>
internal sealed class ProviderAuthException(string code, bool revoked) : Exception(code)
{
    public string Code { get; } = code;
    public bool Revoked { get; } = revoked;
}

/// <summary>
/// The OAuth half of a provider (connected agendas, lot 1). The calendar and contact operations
/// join this port in lots 2 and 5.
/// </summary>
internal interface IConnectedAccountProvider
{
    ConnectedAccountProvider Provider { get; }

    /// <summary>A client id and secret are configured (or the provider is simulated).</summary>
    bool IsAvailable { get; }

    Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri, ProviderConsent consent);

    /// <param name="includeMail">The consent asked for the mailbox: Microsoft's token endpoint must be sent the same scopes.</param>
    Task<ProviderTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, bool includeMail, CancellationToken ct);

    /// <param name="includeMail">
    /// The account reads its mail. Asking Microsoft for a scope the user never granted fails the
    /// refresh — which would mark the account « à reconnecter » — so this follows the account.
    /// </param>
    Task<ProviderTokens> RefreshAsync(string refreshToken, bool includeMail, CancellationToken ct);

    /// <summary>The scopes a token endpoint answered include reading AND sending mail.</summary>
    bool GrantsMail(string grantedScopes);

    Task<ProviderProfile> GetProfileAsync(string accessToken, CancellationToken ct);

    /// <summary>Best effort: a revocation that fails leaves a token nobody holds any more.</summary>
    Task RevokeAsync(string refreshToken, CancellationToken ct);

    // ── Agendas (lot 2) ──────────────────────────────────────────────────

    Task<IReadOnlyList<ProviderCalendar>> ListCalendarsAsync(string accessToken, CancellationToken ct);

    /// <summary>
    /// One page of an agenda's events. <paramref name="cursor"/> null opens the window; then the
    /// page's <see cref="ProviderEventPage.NextPage"/> until a <see cref="ProviderEventPage.DeltaCursor"/>
    /// comes back, which the next run passes to read only what changed.
    /// </summary>
    Task<ProviderEventPage> ReadEventsAsync(
        string accessToken, string calendarId, string? cursor, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct);

    // ── Copies of SANKORÉ rendez-vous (lot 4) ────────────────────────────

    /// <summary>Creates the event and answers its id.</summary>
    Task<string> CreateEventAsync(string accessToken, string calendarId, ProviderEventDraft draft, CancellationToken ct);

    /// <summary>False when the provider no longer has the event (deleted by hand): the caller creates it again.</summary>
    Task<bool> UpdateEventAsync(string accessToken, string calendarId, string eventId, ProviderEventDraft draft, CancellationToken ct);

    /// <summary>An event already gone is not an error.</summary>
    Task DeleteEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct);
}

/// <summary>The provider registered for a provider value — the real one, or the simulated one in Development.</summary>
internal sealed class ConnectedAccountProviders(IEnumerable<IConnectedAccountProvider> providers)
{
    public IConnectedAccountProvider? Find(ConnectedAccountProvider provider) =>
        providers.FirstOrDefault(p => p.Provider == provider && p.IsAvailable);

    public IReadOnlyList<ConnectedAccountProvider> Available() =>
        providers.Where(p => p.IsAvailable).Select(p => p.Provider).Distinct().ToList();
}

/// <summary>The token-endpoint conversation both providers share: a form POST and a JSON answer.</summary>
internal abstract class OAuthProviderBase(HttpClient http)
{
    public abstract ConnectedAccountProvider Provider { get; }

    protected abstract Uri TokenEndpoint { get; }

    protected abstract string? ClientId { get; }

    protected abstract string? ClientSecret { get; }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public Task<ProviderTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, bool includeMail, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri.ToString(),
        }, includeMail, ct);

    public Task<ProviderTokens> RefreshAsync(string refreshToken, bool includeMail, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, includeMail, ct);

    protected virtual void AddTokenParameters(IDictionary<string, string> form, bool includeMail) { }

    /// <summary>Every scope of <paramref name="required"/> is among <paramref name="granted"/>, compared on the last path segment.</summary>
    protected static bool HasScopes(string granted, IEnumerable<string> required)
    {
        // Microsoft answers « https://graph.microsoft.com/Mail.Send », and we asked « Mail.Send ».
        var names = granted.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s[(s.LastIndexOf('/') + 1)..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return required.All(r => names.Contains(r[(r.LastIndexOf('/') + 1)..]));
    }

    private async Task<ProviderTokens> TokenAsync(Dictionary<string, string> form, bool includeMail, CancellationToken ct)
    {
        form["client_id"] = ClientId!;
        form["client_secret"] = ClientSecret!;
        AddTokenParameters(form, includeMail);

        HttpResponseMessage response;

        try
        {
            response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        }
        catch (HttpRequestException)
        {
            throw new ProviderAuthException("provider_unreachable", revoked: false);
        }

        using (response)
        {
            using var doc = await ReadJsonAsync(response, ct);
            var root = doc?.RootElement;

            if (!response.IsSuccessStatusCode || root is null)
            {
                // The OAuth error code, never its description: the description may echo the request.
                var error = root?.TryGetProperty("error", out var e) == true ? e.GetString() ?? "unknown" : $"http_{(int)response.StatusCode}";
                throw new ProviderAuthException(error, revoked: error is "invalid_grant" or "unauthorized_client" or "consent_required");
            }

            var access = root.Value.GetProperty("access_token").GetString()!;
            var refresh = root.Value.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            var expiresIn = root.Value.TryGetProperty("expires_in", out var x) && x.TryGetInt32(out var seconds) ? seconds : 3600;
            var scopes = root.Value.TryGetProperty("scope", out var s) ? s.GetString() ?? string.Empty : string.Empty;

            return new ProviderTokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn), scopes);
        }
    }

    protected async Task<JsonElement> GetJsonAsync(Uri uri, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, ct);
        using var doc = await ReadJsonAsync(response, ct);

        if (!response.IsSuccessStatusCode || doc is null)
            throw new ProviderAuthException($"profile_http_{(int)response.StatusCode}", revoked: response.StatusCode == HttpStatusCode.Unauthorized);

        return doc.RootElement.Clone();
    }

    protected HttpClient Http => http;

    /// <summary>A GET whose status the caller reads — Google answers 410 for a cursor it forgot.</summary>
    protected async Task<(HttpStatusCode Status, JsonElement? Body)> GetAsync(Uri uri, string accessToken, CancellationToken ct, string? prefer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (prefer is not null) request.Headers.TryAddWithoutValidation("Prefer", prefer);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new ProviderAuthException("provider_unreachable", revoked: false);
        }

        using (response)
        {
            using var doc = await ReadJsonAsync(response, ct);
            return (response.StatusCode, doc?.RootElement.Clone());
        }
    }

    /// <summary>A write (POST, PATCH, DELETE) whose status the caller reads.</summary>
    protected async Task<(HttpStatusCode Status, JsonElement? Body)> SendAsync(
        HttpMethod method, Uri uri, string accessToken, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new ProviderAuthException("provider_unreachable", revoked: false);
        }

        using (response)
        {
            using var doc = await ReadJsonAsync(response, ct);
            return (response.StatusCode, doc?.RootElement.Clone());
        }
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Microsoft identity platform v2 + Graph, for work AND personal accounts (<c>common</c>).
///
/// <para>
/// <b>No revocation endpoint.</b> Microsoft offers none for one application's refresh token — only
/// <c>revokeSignInSessions</c>, which signs the user out of EVERYTHING. Removing an account deletes
/// the tokens here; the user withdraws the consent itself at myaccount.microsoft.com.
/// </para>
/// </summary>
internal sealed partial class MicrosoftConnectedAccountProvider(HttpClient http, IOptions<ConnectedAccountOptions> options)
    : OAuthProviderBase(http), IConnectedAccountProvider
{
    /// <summary>The scopes of every lot at once, so the user consents once (calendars read-write for lot 4, contacts for lot 5).</summary>
    public const string Scopes = "openid email profile offline_access User.Read Calendars.ReadWrite Contacts.Read";

    /// <summary>
    /// The mailbox (connected mailbox), asked only when the user turns it on: read and mark read,
    /// and send as the user. Some organisations require an administrator to approve them.
    /// </summary>
    public const string MailScopes = "Mail.ReadWrite Mail.Send";

    private static string ScopesFor(bool includeMail) => includeMail ? $"{Scopes} {MailScopes}" : Scopes;

    public bool GrantsMail(string grantedScopes) => HasScopes(grantedScopes, MailScopes.Split(' '));

    private ConnectedAccountOptions.MicrosoftOptions Settings => options.Value.Microsoft;

    public override ConnectedAccountProvider Provider => ConnectedAccountProvider.Microsoft;

    protected override Uri TokenEndpoint => new($"https://login.microsoftonline.com/{Settings.Tenant}/oauth2/v2.0/token");

    protected override string? ClientId => Settings.ClientId;

    protected override string? ClientSecret => Settings.ClientSecret;

    protected override void AddTokenParameters(IDictionary<string, string> form, bool includeMail) => form["scope"] = ScopesFor(includeMail);

    public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri, ProviderConsent consent)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = Settings.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.ToString(),
            ["response_mode"] = "query",
            ["scope"] = ScopesFor(consent.IncludeMail),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            // Several Microsoft accounts per user is the point: always let them choose.
            ["prompt"] = "select_account",
        };

        if (!string.IsNullOrWhiteSpace(consent.LoginHint)) query["login_hint"] = consent.LoginHint;

        return new(QueryHelpers.AddQueryString($"https://login.microsoftonline.com/{Settings.Tenant}/oauth2/v2.0/authorize", query));
    }

    public async Task<ProviderProfile> GetProfileAsync(string accessToken, CancellationToken ct)
    {
        var me = await GetJsonAsync(new Uri("https://graph.microsoft.com/v1.0/me?$select=id,mail,userPrincipalName,displayName"), accessToken, ct);

        var email = me.TryGetProperty("mail", out var m) && m.GetString() is { Length: > 0 } mail
            ? mail
            : me.GetProperty("userPrincipalName").GetString()!;

        return new ProviderProfile(me.GetProperty("id").GetString()!, email, me.TryGetProperty("displayName", out var d) ? d.GetString() : null);
    }

    public Task RevokeAsync(string refreshToken, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Google OAuth 2.0 + OpenID Connect, for Gmail and Google Workspace accounts.</summary>
internal sealed partial class GoogleConnectedAccountProvider(HttpClient http, IOptions<ConnectedAccountOptions> options)
    : OAuthProviderBase(http), IConnectedAccountProvider
{
    public const string Scopes =
        "openid email profile "
        + "https://www.googleapis.com/auth/calendar.readonly "
        + "https://www.googleapis.com/auth/calendar.events "
        + "https://www.googleapis.com/auth/contacts.readonly";

    /// <summary>
    /// The mailbox (connected mailbox), asked only when the user turns it on. <c>gmail.modify</c>
    /// reads, marks read and sends; it is a RESTRICTED scope: past testing, Google requires the
    /// application to pass a CASA security assessment before strangers may grant it.
    /// </summary>
    public const string MailScopes = "https://www.googleapis.com/auth/gmail.modify";

    public bool GrantsMail(string grantedScopes) => HasScopes(grantedScopes, [MailScopes]);

    private ConnectedAccountOptions.GoogleOptions Settings => options.Value.Google;

    public override ConnectedAccountProvider Provider => ConnectedAccountProvider.Google;

    protected override Uri TokenEndpoint => new("https://oauth2.googleapis.com/token");

    protected override string? ClientId => Settings.ClientId;

    protected override string? ClientSecret => Settings.ClientSecret;

    public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri, ProviderConsent consent)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = Settings.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = consent.IncludeMail ? $"{Scopes} {MailScopes}" : Scopes,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            // offline + consent: Google only issues a refresh token on a consent screen.
            ["access_type"] = "offline",
            ["prompt"] = "consent select_account",
            ["include_granted_scopes"] = "true",
        };

        if (!string.IsNullOrWhiteSpace(consent.LoginHint)) query["login_hint"] = consent.LoginHint;

        return new(QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", query));
    }

    public async Task<ProviderProfile> GetProfileAsync(string accessToken, CancellationToken ct)
    {
        var me = await GetJsonAsync(new Uri("https://openidconnect.googleapis.com/v1/userinfo"), accessToken, ct);
        return new ProviderProfile(me.GetProperty("sub").GetString()!, me.GetProperty("email").GetString()!, me.TryGetProperty("name", out var n) ? n.GetString() : null);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            using var response = await Http.PostAsync(
                "https://oauth2.googleapis.com/revoke",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }), ct);
        }
        catch (HttpRequestException)
        {
            // Best effort: the token is deleted here either way.
        }
    }
}

/// <summary>
/// A simulated provider for Development (<c>UseFakeProviders</c>): the « consent screen » is our
/// own callback, reached at once with a fake code, so the whole round trip — state, PKCE, tokens,
/// vault, account row — runs on a laptop with no Microsoft or Google registration.
/// </summary>
internal sealed partial class FakeConnectedAccountProvider(ConnectedAccountProvider provider, IHostEnvironment environment) : IConnectedAccountProvider
{
    public ConnectedAccountProvider Provider { get; } = provider;

    /// <summary>An account this simulation created — its external id is « fake-… » (see <see cref="GetProfileAsync"/>).</summary>
    public static bool IsSimulated(ConnectedAccount account) =>
        account.ExternalAccountId.StartsWith("fake-", StringComparison.Ordinal);

    /// <summary>Development only, whatever the configuration says: a simulated account in production would look real.</summary>
    public bool IsAvailable => environment.IsDevelopment();

    public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri, ProviderConsent consent) =>
        new(QueryHelpers.AddQueryString(redirectUri.ToString(), new Dictionary<string, string?>
        {
            ["code"] = $"fake-{codeChallenge[..12]}",
            ["state"] = state,
        }));

    public Task<ProviderTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, bool includeMail, CancellationToken ct) =>
        Task.FromResult(new ProviderTokens($"fake-access-{Guid.NewGuid():N}", $"fake-refresh-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddHours(1), SimulatedScopes(includeMail)));

    public Task<ProviderTokens> RefreshAsync(string refreshToken, bool includeMail, CancellationToken ct) =>
        Task.FromResult(new ProviderTokens($"fake-access-{Guid.NewGuid():N}", null, DateTimeOffset.UtcNow.AddHours(1), SimulatedScopes(includeMail)));

    /// <summary>A simulated consent grants whatever was asked.</summary>
    public bool GrantsMail(string grantedScopes) => grantedScopes.Split(' ').Contains("simulated-mail");

    private static string SimulatedScopes(bool includeMail) => includeMail ? "simulated simulated-mail" : "simulated";

    public Task<ProviderProfile> GetProfileAsync(string accessToken, CancellationToken ct)
    {
        // A new simulated account per consent: several accounts per provider is what the screen shows.
        var id = accessToken[^8..];
        var domain = Provider == ConnectedAccountProvider.Microsoft ? "outlook.test" : "gmail.test";
        return Task.FromResult(new ProviderProfile($"fake-{id}", $"compte.{id}@{domain}", "Compte simulé"));
    }

    public Task RevokeAsync(string refreshToken, CancellationToken ct) => Task.CompletedTask;
}
