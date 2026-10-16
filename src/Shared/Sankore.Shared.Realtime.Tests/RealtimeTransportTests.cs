namespace Sankore.Shared.Realtime.Tests;

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Shared.Kernel.Realtime;
using Xunit;

/// <summary>
/// The four things the push channel has to get right, and which have no visible symptom when they
/// are wrong (TEL-B-10).
/// </summary>
public sealed class RealtimeTransportTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid User = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    // ── Group naming ──────────────────────────────────────────────────────────

    [Fact]
    public void A_group_name_carries_the_tenant_so_one_user_id_in_two_tenants_cannot_collide()
    {
        // The backplane is one shared Redis channel. A group named after a user id alone would
        // deliver one institution's pushes to another's wherever an id existed in both.
        SankoreRealtimeHub.UserGroup(TenantA, User)
            .Should().NotBe(SankoreRealtimeHub.UserGroup(TenantB, User));
    }

    [Fact]
    public void A_group_name_is_stable_for_the_same_pair()
    {
        // The pusher and the connection compute it independently. Any instability — a culture, a
        // clock, a casing — and a push lands in a group nobody joined, silently.
        SankoreRealtimeHub.UserGroup(TenantA, User)
            .Should().Be(SankoreRealtimeHub.UserGroup(TenantA, User));
    }

    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void An_authenticated_principal_with_both_claims_resolves()
    {
        var identity = RealtimeIdentity.From(Principal(TenantA.ToString(), User.ToString()));

        identity.Should().NotBeNull();
        identity!.TenantId.Should().Be(TenantA);
        identity.UserId.Should().Be(User);
    }

    [Theory]
    [InlineData(null, "cccccccc-0000-0000-0000-000000000003")]
    [InlineData("aaaaaaaa-0000-0000-0000-000000000001", null)]
    [InlineData("not-a-guid", "cccccccc-0000-0000-0000-000000000003")]
    [InlineData("00000000-0000-0000-0000-000000000000", "cccccccc-0000-0000-0000-000000000003")]
    [InlineData("aaaaaaaa-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000000")]
    public void A_principal_missing_or_malforming_either_claim_resolves_to_nothing(
        string? tenant, string? user)
    {
        // Guid.Empty is refused on both: it is the SYSTEM placeholder, and a connection joining
        // its group would receive whatever a background job pushed there.
        RealtimeIdentity.From(Principal(tenant, user)).Should().BeNull();
    }

    [Fact]
    public void An_unauthenticated_principal_resolves_to_nothing_rather_than_throwing()
    {
        // It runs in OnConnectedAsync, where an exception is a connection that fails with no
        // diagnosis on either end.
        RealtimeIdentity.From(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeNull();
        RealtimeIdentity.From(null).Should().BeNull();
    }

    // ── The notifier ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_push_goes_to_the_users_own_group_and_carries_the_event_name()
    {
        var clients = Substitute.For<IHubClients>();
        var group = Substitute.For<IClientProxy>();
        clients.Group(Arg.Any<string>()).Returns(group);

        var hub = Substitute.For<IHubContext<SankoreRealtimeHub>>();
        hub.Clients.Returns(clients);

        var sut = new SignalRRealtimeNotifier(hub, NullLogger<SignalRRealtimeNotifier>.Instance);

        var pushed = await sut.PushToUserAsync(
            TenantA, User, "telephony.call.ringing", new { callId = Guid.Empty },
            CancellationToken.None);

        pushed.Should().BeTrue();
        clients.Received(1).Group(SankoreRealtimeHub.UserGroup(TenantA, User));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_push_naming_no_tenant_or_no_user_is_dropped(bool emptyTenant, bool emptyUser)
    {
        var clients = Substitute.For<IHubClients>();
        var hub = Substitute.For<IHubContext<SankoreRealtimeHub>>();
        hub.Clients.Returns(clients);

        var sut = new SignalRRealtimeNotifier(hub, NullLogger<SignalRRealtimeNotifier>.Instance);

        var pushed = await sut.PushToUserAsync(
            emptyTenant ? Guid.Empty : TenantA,
            emptyUser ? Guid.Empty : User,
            "telephony.call.ringing", new { }, CancellationToken.None);

        pushed.Should().BeFalse();
        clients.DidNotReceiveWithAnyArgs().Group(default!);
    }

    [Fact]
    public async Task A_transport_failure_answers_false_and_never_throws()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("redis"));

        var hub = Substitute.For<IHubContext<SankoreRealtimeHub>>();
        hub.Clients.Returns(clients);

        var sut = new SignalRRealtimeNotifier(hub, NullLogger<SignalRRealtimeNotifier>.Instance);

        // Its callers are journalling a conversation that actually happened. A hub whose backplane
        // is unreachable must not take the journal down with it.
        var pushed = await sut.PushToUserAsync(
            TenantA, User, "telephony.call.ringing", new { }, CancellationToken.None);

        pushed.Should().BeFalse();
    }

    [Fact]
    public async Task The_null_notifier_answers_false_rather_than_throwing()
    {
        // A host that pushes nothing still journals calls, and its callers must have one path.
        var pushed = await new NullRealtimeNotifier().PushToUserAsync(
            TenantA, User, "telephony.call.ringing", new { }, CancellationToken.None);

        pushed.Should().BeFalse();
    }

    // ── The websocket token ───────────────────────────────────────────────────

    [Fact]
    public async Task The_query_string_token_is_read_on_the_hub_path()
    {
        // The browser's WebSocket API cannot set an Authorization header, so SignalR puts the
        // token in the query. Without this the hub authenticates nobody.
        var ctx = await ReceiveAsync(SankoreRealtimeHub.Path, "a-token");

        ctx.Token.Should().Be("a-token");
    }

    [Theory]
    [InlineData("/api/v1/leads")]
    [InlineData("/api/v1/integration/telephony/user-links")]
    [InlineData("/hubs/realtime-but-not-really")]
    public async Task The_query_string_token_is_IGNORED_everywhere_else(string path)
    {
        // A credential in a query string is a credential written into every access log and proxy
        // trace on the way. StartsWithSegments and not Contains, so a crafted path cannot opt a
        // route into it — note the third case, which Contains would have accepted.
        var ctx = await ReceiveAsync(path, "a-token");

        ctx.Token.Should().BeNull();
    }

    [Fact]
    public async Task The_existing_message_handler_still_runs()
    {
        // The platform scheme carries its own events. An options object assigned wholesale here
        // would silently drop them — including the session-revocation check.
        var ran = false;

        var options = new JwtBearerOptions
        {
            Events = new JwtBearerEvents
            {
                OnMessageReceived = _ =>
                {
                    ran = true;

                    return Task.CompletedTask;
                },
            },
        };

        options.UseRealtimeAccessToken();

        await options.Events!.OnMessageReceived(Context(SankoreRealtimeHub.Path, "a-token"));

        ran.Should().BeTrue();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static ClaimsPrincipal Principal(string? tenant, string? user)
    {
        var claims = new List<Claim>();

        if (tenant is not null) claims.Add(new Claim("tenant_id", tenant));
        if (user is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, user));

        // The authentication TYPE is what makes IsAuthenticated true — an identity built with no
        // type is unauthenticated whatever claims it carries.
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
    }

    private static MessageReceivedContext Context(string path, string token)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.QueryString = new QueryString($"?access_token={token}");

        return new MessageReceivedContext(
            http, new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());
    }

    private static async Task<MessageReceivedContext> ReceiveAsync(string path, string token)
    {
        var options = new JwtBearerOptions();
        options.UseRealtimeAccessToken();

        var ctx = Context(path, token);

        await options.Events!.OnMessageReceived(ctx);

        return ctx;
    }
}
