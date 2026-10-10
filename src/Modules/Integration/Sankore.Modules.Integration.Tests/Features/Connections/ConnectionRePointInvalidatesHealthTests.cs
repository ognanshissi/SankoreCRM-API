namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// Editing a connection's coordinates throws away what the last health check said about them.
///
/// <para>
/// <b>The defect.</b> <c>UpdateSettings</c> wrote the new coordinates and left
/// <c>LastHealthStatus</c>, <c>LastHealthAt</c> and <c>LastHealthDetail</c> exactly as they were.
/// So an administrator could move a live Temenos connection's <c>baseUrl</c> to the wrong host, or
/// flip its auth mode, and the operations screen kept reporting it healthy — describing a
/// configuration that no longer existed — while every command failed one at a time somewhere else.
/// A stale green column is worse than an empty one, because it is the column somebody checks
/// first and the one that ends the investigation.
/// </para>
///
/// <para>
/// It also quietly undermined the argument the whole blocked-adapter design rests on. INT-28, -31
/// and -32 are safe because their health check can never pass, so their connections can never be
/// activated and no command can ever be queued against them. That chain is only as strong as
/// "a passed health check describes the current configuration" — and nothing was keeping it true.
/// </para>
///
/// <para>
/// <see cref="IntegrationConnection.IsActive"/> is left untouched on purpose, and the last test
/// here pins that: this method also carries the connection's NAME, so deactivating on edit would
/// stop a tenant's integration because somebody fixed a typo in a label.
/// </para>
/// </summary>
public sealed class ConnectionRePointInvalidatesHealthTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Actor = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Editor = new("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Agent = new("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    private static TimeProvider Clock => new FixedClock(Now);

    private static IntegrationConnection Healthy(
        IntegrationMode mode = IntegrationMode.Api, ConnectionSettings? settings = null)
    {
        var connection = IntegrationConnection.Create(
            tenantId: Tenant,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Temenos,
            mode: mode,
            name: "CBS principal",
            settings: settings ?? Temenos("https://cbs.example.ci/api/"),
            createdBy: Actor,
            clock: Clock);

        connection.RecordHealth(
            IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(80), Now), Clock);

        connection.Activate(Actor, Clock).IsSuccess.Should().BeTrue();

        return connection;
    }

    private static TemenosSettings Temenos(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        AuthMode = TemenosAuthMode.OAuthClientCredentials,
        TokenEndpoint = "https://cbs.example.ci/oauth/token",
        OAuthClientId = "sankore",
        CompanyId = "CI0010001",
    };

    [Fact]
    public void A_new_base_url_throws_the_verdict_away()
    {
        var connection = Healthy();

        connection.UpdateSettings(
            connection.Name, connection.Mode, Temenos("https://elsewhere.example.ci/api/"),
            relayAgentId: null, updatedBy: Editor, clock: Clock);

        // All three columns, not just the boolean: a null verdict beside a timestamp from this
        // morning still invites the reader to conclude the connection was checked.
        connection.LastHealthStatus.Should().BeNull();
        connection.LastHealthAt.Should().BeNull();
        connection.LastHealthDetail.Should().Contain("re-pointed");

        // The consequence that matters, and the one the blocked adapters depend on.
        connection.HasPassedHealthCheck.Should().BeFalse();
    }

    [Fact]
    public void A_new_mode_throws_the_verdict_away()
    {
        var connection = Healthy();

        // The exact path that made the relay hole reachable: a Temenos connection checked and
        // activated in Api mode, then switched to Relay. Nothing re-examined it, so it stayed
        // active and green — and its commands were dispatched straight out of this process.
        connection.UpdateSettings(
            connection.Name, IntegrationMode.Relay, Temenos("https://cbs.example.ci/api/"),
            relayAgentId: Agent, updatedBy: Editor, clock: Clock);

        connection.HasPassedHealthCheck.Should().BeFalse();
        connection.Mode.Should().Be(IntegrationMode.Relay);
    }

    [Fact]
    public void A_new_relay_agent_throws_the_verdict_away()
    {
        var connection = Healthy(IntegrationMode.Relay);

        connection.UpdateSettings(
            connection.Name, IntegrationMode.Relay, Temenos("https://cbs.example.ci/api/"),
            relayAgentId: Agent, updatedBy: Editor, clock: Clock);

        // The settings and the mode are untouched; only the machine on the other end changed.
        // A verdict obtained through a different agent says nothing about this one.
        connection.HasPassedHealthCheck.Should().BeFalse();
    }

    [Fact]
    public void A_pure_rename_keeps_the_verdict()
    {
        var connection = Healthy();

        connection.UpdateSettings(
            "CBS principal (Abidjan)", connection.Mode, Temenos("https://cbs.example.ci/api/"),
            relayAgentId: null, updatedBy: Editor, clock: Clock);

        // Nothing that was checked has changed. Asking for a fresh health check after a label
        // correction is how you teach an administrator to click past the warning — and the next
        // time it means something, they will.
        connection.LastHealthStatus.Should().BeTrue();
        connection.LastHealthAt.Should().Be(Now);
        connection.HasPassedHealthCheck.Should().BeTrue();
        connection.Name.Should().Be("CBS principal (Abidjan)");
    }

    [Fact]
    public void Settings_that_are_equal_by_value_are_not_a_re_point()
    {
        var connection = Healthy();

        // A different instance carrying identical values — which is what a round trip through the
        // HTTP converter produces on a form the administrator opened and saved unchanged. Treating
        // instance identity as a change would invalidate the verdict on every no-op save.
        connection.UpdateSettings(
            connection.Name, connection.Mode, Temenos("https://cbs.example.ci/api/"),
            relayAgentId: null, updatedBy: Editor, clock: Clock);

        connection.HasPassedHealthCheck.Should().BeTrue();
    }

    [Fact]
    public void A_re_point_does_not_deactivate_a_live_connection()
    {
        var connection = Healthy();

        connection.UpdateSettings(
            connection.Name, connection.Mode, Temenos("https://elsewhere.example.ci/api/"),
            relayAgentId: null, updatedBy: Editor, clock: Clock);

        // The deliberate half of the decision. Deactivating is the safer reflex and the wrong
        // behaviour here: a connection that was draining a queue keeps draining it, and a wrong
        // edit surfaces in the rejection queue rather than as an outage nobody asked for. What the
        // invalidated verdict does buy is that re-activating it later requires a real check.
        connection.IsActive.Should().BeTrue();

        connection.Deactivate(Editor, Clock);
        connection.Activate(Editor, Clock).Error.Should().Be(IntegrationErrors.ConnectionNotHealthy);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
