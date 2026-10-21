namespace Sankore.Modules.Integration.Tests.Features.ConnectedAccounts;

using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.ConnectedAccounts;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Connected agendas, lot 1: the OAuth round trip, the tokens, the account's life.</summary>
public sealed class ConnectedAccountsTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly InMemorySecrets _secrets = new();
    private readonly IConfiguration _config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "a-dev-only-signing-key-that-is-long-enough-for-hkdf",
            ["Cors:AllowedOrigins:0"] = "https://crm.example.com",
        })
        .Build();

    public ConnectedAccountsTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);
        _currentUser.Id.Returns(_me);
        _currentUser.TenantId.Returns(_tenant);
    }

    public void Dispose() => _factory.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The vault, in memory, with the expiry it records.</summary>
    private sealed class InMemorySecrets : ISecretsModule
    {
        public ConcurrentDictionary<SecretKey, (string Value, DateTimeOffset? ExpiresAt)> Entries { get; } = new();

        public Task SetAsync(SecretKey key, string value, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
        {
            Entries[key] = (value, expiresAt);
            return Task.CompletedTask;
        }

        public Task<SecretHint?> GetHintAsync(SecretKey key, CancellationToken ct = default) =>
            Task.FromResult(Entries.TryGetValue(key, out var e) ? new SecretHint(key.Name, "***", e.ExpiresAt) : null);

        public Task<string?> GetValueAsync(SecretKey key, CancellationToken ct = default) =>
            Task.FromResult(Entries.TryGetValue(key, out var e) ? e.Value : null);

        public Task DeleteAllAsync(Guid tenantId, string scope, Guid entityId, CancellationToken ct = default)
        {
            foreach (var key in Entries.Keys.Where(k => k.TenantId == tenantId && k.Scope == scope && k.EntityId == entityId).ToList())
                Entries.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>A scripted provider: refreshes succeed or are refused on demand.</summary>
    private sealed class ScriptedProvider(ConnectedAccountProvider provider) : IConnectedAccountProvider
    {
        public bool RefuseRefresh { get; set; }
        public int Refreshes { get; private set; }
        public ConnectedAccountProvider Provider { get; } = provider;
        public bool IsAvailable => true;

        public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri) =>
            new($"{redirectUri}?code=the-code&state={Uri.EscapeDataString(state)}&challenge={codeChallenge}");

        public Task<ProviderTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct)
        {
            // The verifier must hash to the challenge sent with the authorization URL — PKCE's whole point.
            LastVerifier = codeVerifier;
            return Task.FromResult(new ProviderTokens("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1), "openid Calendars.ReadWrite"));
        }

        public string? LastVerifier { get; private set; }

        public Task<ProviderTokens> RefreshAsync(string refreshToken, CancellationToken ct)
        {
            Refreshes++;
            if (RefuseRefresh) throw new ProviderAuthException("invalid_grant", revoked: true);
            return Task.FromResult(new ProviderTokens("access-2", null, DateTimeOffset.UtcNow.AddHours(1), "openid"));
        }

        public Task<ProviderProfile> GetProfileAsync(string accessToken, CancellationToken ct) =>
            Task.FromResult(new ProviderProfile("ext-42", "awa@example.com", "Awa"));

        public Task RevokeAsync(string refreshToken, CancellationToken ct)
        {
            Revoked = refreshToken;
            return Task.CompletedTask;
        }

        public string? Revoked { get; private set; }

        public Task<IReadOnlyList<ProviderCalendar>> ListCalendarsAsync(string accessToken, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderCalendar>>([]);

        public Task<ProviderEventPage> ReadEventsAsync(
            string accessToken, string calendarId, string? cursor, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct) =>
            Task.FromResult(new ProviderEventPage([], [], null, "delta"));

        public Task<string> CreateEventAsync(string accessToken, string calendarId, ProviderEventDraft draft, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpdateEventAsync(string accessToken, string calendarId, string eventId, ProviderEventDraft draft, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct) => throw new NotSupportedException();
    }

    private readonly ScriptedProvider _google = new(ConnectedAccountProvider.Google);

    // ── PKCE ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_code_challenge_is_RFC_7636_S256()
        => ConnectedAccountOAuth.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
            .Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");

    [Fact]
    public void The_verifier_is_derived_from_the_state_and_never_the_same_for_two_states()
    {
        var oauth = new ConnectedAccountOAuth(_config);
        var state = ConnectedAccountOAuth.NewState();

        oauth.CodeVerifier(state).Should().Be(oauth.CodeVerifier(state)).And.HaveLength(43);
        oauth.CodeVerifier(state).Should().NotBe(oauth.CodeVerifier(ConnectedAccountOAuth.NewState()));
    }

    // ── The round trip ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_consent_round_trip_creates_the_account_stores_its_tokens_and_returns_to_the_front()
    {
        var url = await StartAsync("https://crm.example.com", label: "Personnel");
        var response = await CallbackAsync(url);

        response.Location.Should().StartWith("https://crm.example.com/settings/account/agendas?connexion=ok&compte=");

        await using var db = _factory.CreateContext();
        var account = await db.ConnectedAccounts.SingleAsync();
        account.UserId.Should().Be(_me);
        account.Label.Should().Be("Personnel");
        account.Email.Should().Be("awa@example.com");
        account.Status.Should().Be(ConnectedAccountStatus.Active);

        _secrets.Entries[ConnectedAccountSecrets.RefreshToken(_tenant, account.Id)].Value.Should().Be("refresh-1");
        _secrets.Entries[ConnectedAccountSecrets.AccessToken(_tenant, account.Id)].Value.Should().Be("access-1");

        var challenge = new Uri(url).Query.Split('&').Single(p => p.StartsWith("challenge=", StringComparison.Ordinal))["challenge=".Length..];
        ConnectedAccountOAuth.CodeChallenge(_google.LastVerifier!).Should().Be(challenge, "the verifier sent at the exchange matches the challenge sent at the start");

        (await db.ConnectedAccountOAuthStates.IgnoreQueryFilters().SingleAsync()).ConsumedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_state_works_once_and_a_replayed_callback_creates_nothing()
    {
        var url = await StartAsync("https://crm.example.com");

        (await CallbackAsync(url)).Location.Should().Contain("connexion=ok");
        (await CallbackAsync(url)).Location.Should().EndWith("connexion=expire");

        await using var db = _factory.CreateContext();
        (await db.ConnectedAccounts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Connecting_the_same_account_again_reconnects_it_instead_of_adding_a_second()
    {
        await CallbackAsync(await StartAsync("https://crm.example.com"));
        await using (var db = _factory.CreateContext())
        {
            var account = await db.ConnectedAccounts.AsTracking().SingleAsync();
            account.MarkNeedsReconnect("invalid_grant");
            await db.SaveChangesAsync();
        }

        await CallbackAsync(await StartAsync("https://crm.example.com"));

        await using var read = _factory.CreateContext();
        (await read.ConnectedAccounts.SingleAsync()).Status.Should().Be(ConnectedAccountStatus.Active);
    }

    [Fact]
    public async Task A_refused_consent_and_an_unknown_state_go_nowhere_useful()
    {
        var url = await StartAsync("https://crm.example.com");
        var state = new Uri(url).Query.Split('&').Single(p => p.TrimStart('?').StartsWith("state=", StringComparison.Ordinal)).Split('=')[1];

        (await CallbackRawAsync($"state={state}&error=access_denied")).Location.Should().EndWith("connexion=refuse");

        var unknown = await CallbackRawAsync("state=forged&code=x");
        unknown.Location.Should().BeNull("an unknown state has no origin to return to — a bare 400, never a redirect");
        unknown.Status.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task A_consent_cannot_be_started_from_an_origin_outside_the_allow_list()
    {
        var result = await Handler().Handle(
            new StartConnectedAccountCommand("Google", null, "https://evil.example", "https://api.example.com"), CancellationToken.None);

        result.Error.Should().Be(ConnectedAccountErrors.OriginNotAllowed);
    }

    // ── Tokens ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_expired_access_token_is_refreshed_and_a_refused_refresh_asks_for_a_reconnection()
    {
        await CallbackAsync(await StartAsync("https://crm.example.com"));

        await using var db = _factory.CreateContext();
        var account = await db.ConnectedAccounts.AsTracking().SingleAsync();
        var tokens = Tokens(db);

        // Fresh: served from the vault, no call to the provider.
        (await tokens.GetAccessTokenAsync(account, CancellationToken.None)).Value.Should().Be("access-1");
        _google.Refreshes.Should().Be(0);

        _secrets.Entries[ConnectedAccountSecrets.AccessToken(_tenant, account.Id)] = ("access-1", DateTimeOffset.UtcNow.AddSeconds(30));
        (await tokens.GetAccessTokenAsync(account, CancellationToken.None)).Value.Should().Be("access-2");
        _secrets.Entries[ConnectedAccountSecrets.RefreshToken(_tenant, account.Id)].Value.Should().Be("refresh-1", "a provider that issues no new refresh token keeps the old one");

        _secrets.Entries[ConnectedAccountSecrets.AccessToken(_tenant, account.Id)] = ("access-2", DateTimeOffset.UtcNow.AddSeconds(-1));
        _google.RefuseRefresh = true;

        (await tokens.GetAccessTokenAsync(account, CancellationToken.None)).Error.Should().Be(ConnectedAccountErrors.NeedsReconnect);
        account.Status.Should().Be(ConnectedAccountStatus.NeedsReconnect);
        account.LastError.Should().Be("invalid_grant");
    }

    // ── Rename, remove, isolation ──────────────────────────────────────────────

    [Fact]
    public async Task Removing_revokes_deletes_the_tokens_and_the_account()
    {
        await CallbackAsync(await StartAsync("https://crm.example.com"));

        await using var db = _factory.CreateContext();
        var id = (await db.ConnectedAccounts.SingleAsync()).Id;

        (await new RemoveConnectedAccountHandler(db, _currentUser, _secrets, Providers(), NoCopies(db), NullLogger<RemoveConnectedAccountHandler>.Instance)
            .Handle(new RemoveConnectedAccountCommand(id), CancellationToken.None)).IsSuccess.Should().BeTrue();

        _google.Revoked.Should().Be("refresh-1");
        _secrets.Entries.Keys.Should().NotContain(k => k.EntityId == id);
        (await db.ConnectedAccounts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Nobody_sees_or_touches_a_colleague_s_account()
    {
        await CallbackAsync(await StartAsync("https://crm.example.com"));

        var colleague = Substitute.For<ICurrentUser>();
        colleague.Id.Returns(Guid.NewGuid());
        colleague.TenantId.Returns(_tenant);

        await using var db = _factory.CreateContext();
        var id = (await db.ConnectedAccounts.SingleAsync()).Id;

        (await new ListMyConnectedAccountsHandler(db, colleague, Providers()).Handle(new ListMyConnectedAccountsQuery(), CancellationToken.None))
            .Accounts.Should().BeEmpty();

        (await new RenameConnectedAccountHandler(db, colleague).Handle(new RenameConnectedAccountCommand(id, "Volé"), CancellationToken.None))
            .Error.Should().Be(ConnectedAccountErrors.NotFound);

        (await new RemoveConnectedAccountHandler(db, colleague, _secrets, Providers(), NoCopies(db), NullLogger<RemoveConnectedAccountHandler>.Instance)
            .Handle(new RemoveConnectedAccountCommand(id), CancellationToken.None)).Error.Should().Be(ConnectedAccountErrors.NotFound);
    }

    [Fact]
    public void A_simulated_provider_is_never_available_outside_Development()
    {
        var production = Substitute.For<IHostEnvironment>();
        production.EnvironmentName.Returns("Production");

        new FakeConnectedAccountProvider(ConnectedAccountProvider.Google, production).IsAvailable.Should().BeFalse();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private ConnectedAccountProviders Providers() => new([_google]);

    private ConnectedAccountTokens Tokens(IntegrationDbContext db) =>
        new(db, _secrets, Providers(), NullLogger<ConnectedAccountTokens>.Instance);

    private StartConnectedAccountHandler Handler()
    {
        var db = _factory.CreateContext();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        return new StartConnectedAccountHandler(
            db, _currentUser, Providers(), new ConnectedAccountOAuth(_config), new ConnectedAccountReturnOrigins(_config, env),
            Options.Create(new ConnectedAccountOptions()), new FixedClock(_now));
    }

    private async Task<string> StartAsync(string origin, string? label = null)
    {
        var result = await Handler().Handle(
            new StartConnectedAccountCommand("google", label, origin, "https://api.example.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!.AuthorizationUrl;
    }

    private Task<(string? Location, int Status)> CallbackAsync(string authorizationUrl) =>
        CallbackRawAsync(new Uri(authorizationUrl).Query.TrimStart('?'));

    private async Task<(string? Location, int Status)> CallbackRawAsync(string query)
    {
        await using var db = _factory.CreateContext();

        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("api.example.com");
        http.Request.QueryString = new QueryString("?" + query);
        http.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        var result = await ConnectedAccountCallback.HandleAsync(
            "google", http, db, Providers(), new ConnectedAccountOAuth(_config), Tokens(db), Substitute.For<IAuditWriter>(),
            Options.Create(new ConnectedAccountOptions()), new FixedClock(_now), NullLoggerFactory.Instance, CancellationToken.None);

        await result.ExecuteAsync(http);
        return (http.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null, http.Response.StatusCode);
    }

    private AppointmentCalendarCopies NoCopies(IntegrationDbContext db) => new(
        db, Substitute.For<Sankore.Modules.Leads.PublicApi.ILeadsModule>(), Providers(),
        new ConnectedAccountTokens(db, _secrets, Providers(), NullLogger<ConnectedAccountTokens>.Instance),
        TimeProvider.System, NullLogger<AppointmentCalendarCopies>.Instance);
}
