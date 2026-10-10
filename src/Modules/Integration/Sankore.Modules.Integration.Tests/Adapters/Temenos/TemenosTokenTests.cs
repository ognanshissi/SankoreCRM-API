namespace Sankore.Modules.Integration.Tests.Adapters.Temenos;

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Temenos;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Authentication with automatic renewal (INT-12, criterion 2).
///
/// <para>
/// Split in two halves because the two failures are different. <see cref="TemenosTokenCacheTests"/>
/// exercises the cache in isolation — expiry arithmetic, the renewal margin, the single-flight
/// guard — against a clock a test can move, because waiting an hour for a real token to expire is
/// not a test. <see cref="TemenosTokenRenewalTests"/> exercises the renewal THROUGH the adapter,
/// where the trigger is a 401 from the installation and the thing being asserted is that the
/// request was sent again with a different token.
/// </para>
/// </summary>
public sealed class TemenosTokenCacheTests
{
    private static readonly TemenosTokenScope Scope = new(
        new Guid("11111111-1111-1111-1111-111111111111"),
        new Guid("22222222-2222-2222-2222-222222222222"));

    private static readonly TemenosTokenScope OtherTenant = new(
        new Guid("33333333-3333-3333-3333-333333333333"),
        Scope.ConnectionId);

    private static TemenosTokenCache Cache(TimeProvider clock, TemenosAdapterOptions? options = null)
        => new(clock, Options.Create(options ?? new TemenosAdapterOptions()));

    [Fact]
    public async Task A_cached_token_is_served_without_minting_a_second_one()
    {
        var clock = MovableClock.At(2026, 3, 1);
        var cache = Cache(clock);
        var minted = 0;

        for (var i = 0; i < 5; i++)
        {
            var token = await cache.GetAsync(
                Scope, _ => Mint(ref minted, clock, TimeSpan.FromHours(1)), forceRenew: false, default);

            token.Value.Should().Be("token-1");
        }

        minted.Should().Be(1);
    }

    [Fact]
    public async Task A_token_inside_the_renewal_margin_is_treated_as_expired()
    {
        var clock = MovableClock.At(2026, 3, 1);
        var options = new TemenosAdapterOptions { TokenRenewalMargin = TimeSpan.FromSeconds(60) };
        var cache = Cache(clock, options);
        var minted = 0;

        var first = await cache.GetAsync(
            Scope, _ => Mint(ref minted, clock, TimeSpan.FromMinutes(5)), forceRenew: false, default);

        // Four minutes and thirty seconds later the token is still valid for thirty seconds —
        // inside the margin, and therefore not worth sending: a token that expires in flight comes
        // back as a 401, which this adapter classifies as AuthenticationRefused, a TECHNICAL
        // failure that is never retried and that wakes an administrator.
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(30));

        var second = await cache.GetAsync(
            Scope, _ => Mint(ref minted, clock, TimeSpan.FromMinutes(5)), forceRenew: false, default);

        first.Value.Should().Be("token-1");
        second.Value.Should().Be("token-2");
        minted.Should().Be(2);
    }

    [Fact]
    public async Task A_token_still_outside_the_margin_is_kept()
    {
        var clock = MovableClock.At(2026, 3, 1);
        var cache = Cache(clock, new TemenosAdapterOptions { TokenRenewalMargin = TimeSpan.FromSeconds(60) });
        var minted = 0;

        await cache.GetAsync(Scope, _ => Mint(ref minted, clock, TimeSpan.FromMinutes(5)), false, default);

        clock.Advance(TimeSpan.FromMinutes(3));

        var second = await cache.GetAsync(
            Scope, _ => Mint(ref minted, clock, TimeSpan.FromMinutes(5)), false, default);

        second.Value.Should().Be("token-1");
        minted.Should().Be(1);
    }

    [Fact]
    public async Task A_token_that_does_not_say_when_it_expires_is_cached_for_the_fallback_and_not_for_ever()
    {
        var clock = MovableClock.At(2026, 3, 1);
        var options = new TemenosAdapterOptions
        {
            FallbackTokenLifetime = TimeSpan.FromMinutes(10),
            TokenRenewalMargin = TimeSpan.Zero,
        };
        var cache = Cache(clock, options);
        var minted = 0;

        Task<IntegrationResult<TemenosToken>> MintUndated(CancellationToken _)
            => Task.FromResult(IntegrationResult.Ok(new TemenosToken($"token-{++minted}", ExpiresAt: null)));

        await cache.GetAsync(Scope, MintUndated, false, default);
        clock.Advance(TimeSpan.FromMinutes(9));
        await cache.GetAsync(Scope, MintUndated, false, default);

        minted.Should().Be(1, "nine minutes in, the fallback lifetime has not run out");

        clock.Advance(TimeSpan.FromMinutes(2));
        await cache.GetAsync(Scope, MintUndated, false, default);

        // expires_in is optional in RFC 6749, and a token cached for ever would keep working
        // until an installation rotated its signing key — at which point every tenant's calls
        // would fail at once, with no renewal having ever been exercised.
        minted.Should().Be(2);
    }

    [Fact]
    public async Task Force_renew_discards_the_cached_token()
    {
        var clock = MovableClock.At(2026, 3, 1);
        var cache = Cache(clock);
        var minted = 0;

        await cache.GetAsync(Scope, _ => Mint(ref minted, clock, TimeSpan.FromHours(1)), false, default);
        var renewed = await cache.GetAsync(
            Scope, _ => Mint(ref minted, clock, TimeSpan.FromHours(1)), forceRenew: true, default);

        renewed.Value.Should().Be("token-2");
        minted.Should().Be(2);
    }

    [Fact]
    public async Task Two_tenants_on_one_connection_id_never_share_a_token()
    {
        var clock = MovableClock.At(2026, 3, 1);
        var cache = Cache(clock);
        var minted = 0;

        var first = await cache.GetAsync(Scope, _ => Mint(ref minted, clock, TimeSpan.FromHours(1)), false, default);
        var second = await cache.GetAsync(OtherTenant, _ => Mint(ref minted, clock, TimeSpan.FromHours(1)), false, default);

        // Connection ids are ours and unique, so this can only happen through a bug — but a cache
        // keyed on half an identity is one bug away from a cross-tenant read, and the tenant costs
        // sixteen bytes.
        second.Value.Should().NotBe(first.Value);
        minted.Should().Be(2);
    }

    [Fact]
    public async Task Fifty_concurrent_callers_on_a_cold_cache_mint_exactly_one_token()
    {
        var clock = MovableClock.At(2026, 3, 1);
        var cache = Cache(clock);
        var minted = 0;
        var gate = new TaskCompletionSource();

        async Task<IntegrationResult<TemenosToken>> SlowMint(CancellationToken _)
        {
            // Held open until every caller has arrived, so the race the single-flight guard exists
            // for is actually run rather than happening to be serialised by the scheduler.
            await gate.Task;

            return IntegrationResult.Ok(new TemenosToken(
                $"token-{Interlocked.Increment(ref minted)}", clock.GetUtcNow().AddHours(1)));
        }

        var callers = Enumerable.Range(0, 50)
            .Select(_ => cache.GetAsync(Scope, SlowMint, forceRenew: false, default))
            .ToList();

        gate.SetResult();

        var tokens = await Task.WhenAll(callers);

        // Without the guard: fifty round trips to the authorisation server, fifty tokens issued,
        // and on an installation that invalidates the previous token on issue, forty-nine calls
        // failing with a 401 that reads like wrong credentials.
        minted.Should().Be(1);
        tokens.Should().OnlyContain(t => t.IsSuccess && t.Value == "token-1");
    }

    [Fact]
    public async Task A_failed_minting_keeps_its_own_family_rather_than_becoming_transient()
    {
        var cache = Cache(MovableClock.At(2026, 3, 1));

        var refused = await cache.GetAsync(
            Scope,
            _ => Task.FromResult(IntegrationResult.Technical<TemenosToken>(
                IntegrationErrors.AuthenticationRefused, "refused")),
            forceRenew: false,
            default);

        // Collapsing the families would have the platform retry wrong credentials for ever, or
        // alert an administrator for a blip.
        refused.Family.Should().Be(ErrorFamily.Technical);
        refused.Code.Should().Be(IntegrationErrors.AuthenticationRefused);
    }

    [Fact]
    public async Task A_failed_minting_is_not_cached()
    {
        var clock = MovableClock.At(2026, 3, 1);
        var cache = Cache(clock);
        var attempts = 0;

        Task<IntegrationResult<TemenosToken>> Flaky(CancellationToken _)
        {
            attempts++;

            return attempts == 1
                ? Task.FromResult(IntegrationResult.Transient<TemenosToken>(IntegrationErrors.Unavailable))
                : Task.FromResult(IntegrationResult.Ok(new TemenosToken("good", clock.GetUtcNow().AddHours(1))));
        }

        (await cache.GetAsync(Scope, Flaky, false, default)).IsFailure.Should().BeTrue();
        (await cache.GetAsync(Scope, Flaky, false, default)).Value.Should().Be("good");
    }

    private static Task<IntegrationResult<TemenosToken>> Mint(
        ref int counter, TimeProvider clock, TimeSpan lifetime)
    {
        var token = $"token-{++counter}";

        return Task.FromResult(IntegrationResult.Ok(
            new TemenosToken(token, clock.GetUtcNow().Add(lifetime))));
    }
}

/// <summary>
/// Renewal as the installation triggers it: a 401 on a token we believed was good.
/// </summary>
public sealed class TemenosTokenRenewalTests
{
    [Fact]
    public async Task A_401_renews_the_token_and_sends_the_request_once_more()
    {
        using var harness = TemenosTestHarness.Create();

        // The one piece of information our expiry arithmetic cannot have: a revocation, a rotated
        // signing key, or a clock further out than the margin.
        harness.Transport.UnauthorizedApiCalls = 1;

        var balance = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        balance.IsSuccess.Should().BeTrue("the second attempt carried a freshly minted token");
        harness.Transport.TokenRequests.Should().Be(2);
        harness.Transport.Requests.Should().HaveCount(2);
        harness.Transport.Requests[1].BearerToken.Should().NotBe(harness.Transport.Requests[0].BearerToken);
    }

    [Fact]
    public async Task A_second_401_is_believed_and_reported_as_a_technical_refusal()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.UnauthorizedApiCalls = 2;

        var balance = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        balance.Family.Should().Be(ErrorFamily.Technical,
            "our credentials or our scopes are wrong, and the one thing that cannot fix them is "
            + "calling again");
        balance.Code.Should().Be(IntegrationErrors.AuthenticationRefused);
        balance.IsRetryable.Should().BeFalse();

        harness.Transport.Requests.Should().HaveCount(2, "renewal is one retry and not a policy");
    }

    [Fact]
    public async Task A_renewal_is_journalled_as_one_row_carrying_the_final_status()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.UnauthorizedApiCalls = 1;

        await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        var row = harness.Journal.Rows.Should().ContainSingle().Subject;

        row.Success.Should().BeTrue();
        row.HttpStatus.Should().Be(200);
    }

    [Fact]
    public async Task A_static_token_is_re_read_from_the_vault_after_a_401_so_a_rotation_is_picked_up()
    {
        using var harness = TemenosTestHarness.WithStaticToken("rotated-token");
        harness.Transport.UnauthorizedApiCalls = 1;

        var balance = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        balance.IsSuccess.Should().BeTrue();

        // For a long-lived token the 401 is the ONLY renewal trigger there is, and re-reading the
        // vault is the only way an administrator's paste into INT-03's secret endpoint ever takes
        // effect without a process restart.
        await harness.Secrets.Received(2).GetValueAsync(
            Arg.Any<SecretKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_token_endpoint_that_refuses_the_credentials_is_technical_and_sends_nothing()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.TokenStatus = HttpStatusCode.Unauthorized;

        var balance = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        balance.Family.Should().Be(ErrorFamily.Technical);
        balance.Code.Should().Be(IntegrationErrors.AuthenticationRefused);
        harness.Transport.ApiCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_token_endpoint_that_answers_200_without_a_token_is_a_technical_refusal()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.TokenBody = TemenosFixtures.TokenResponseWithoutToken;

        var balance = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        balance.Code.Should().Be(IntegrationErrors.AuthenticationRefused);
        harness.Transport.ApiCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_OAuth_connection_with_no_token_endpoint_is_refused_as_misconfigured()
    {
        var settings = TemenosTestHarness.DefaultSettings() with { TokenEndpoint = null };
        using var harness = TemenosTestHarness.Create(settings);

        var balance = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        balance.Family.Should().Be(ErrorFamily.Technical,
            "no amount of retrying fills in a token endpoint");
        balance.Code.Should().Be(IntegrationErrors.SettingsInvalid);
    }

    [Fact]
    public async Task Two_calls_in_one_scope_share_one_token()
    {
        using var harness = TemenosTestHarness.Create();

        await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);
        await harness.Adapter.GetAccountsAsync(
            new ExternalId(TemenosFixtures.SeededCustomerId), CancellationToken.None);

        harness.Transport.TokenRequests.Should().Be(1);
    }
}
