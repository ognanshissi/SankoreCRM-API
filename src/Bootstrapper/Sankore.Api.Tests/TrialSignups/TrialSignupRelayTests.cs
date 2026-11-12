namespace Sankore.Api.Tests.TrialSignups;

using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Api.Features.TrialSignups;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The public door of the free trial: the registry decides the form, this API relays its answer and
/// queues the e-mails it lists; and the job that tells requesters the platform's decision.
/// </summary>
public sealed class TrialSignupRelayTests
{
    private readonly ITrialSignupRegistry _registry = Substitute.For<ITrialSignupRegistry>();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly List<QueueEmailRequest> _mails = [];

    public TrialSignupRelayTests()
    {
        _notifications.QueueEmailAsync(Arg.Do<QueueEmailRequest>(_mails.Add), Arg.Any<CancellationToken>())
            .Returns(Result<Guid>.Ok(Guid.NewGuid()));
    }

    private TrialSignupRelay Relay() => new(_registry, _notifications, NullLogger<TrialSignupRelay>.Instance);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<(int Status, string Body)> RunAsync(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    // ── Relay ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_registry_answer_is_returned_as_is_and_its_mails_are_queued_for_the_platform()
    {
        _registry.SubmitForPublicAsync(Arg.Any<JsonElement>(), "198.51.100.7", "Mozilla", Arg.Any<CancellationToken>())
            .Returns(new RegistryCall<RegistryPublicAnswer>(new RegistryPublicAnswer(202,
                Json("""{"status":"CONFIRMATION_SENT","fqdn":null}"""),
                [new RegistryMail("trial.signup-confirm", "awa@caisse.ci", "Awa", "EN",
                    new Dictionary<string, string> { ["confirm_url"] = "https://talisoft.net/en/trial/confirmation#t=x" },
                    "trial.signup-confirm:1")]), null));

        var (status, body) = await RunAsync(await Relay().SubmitAsync(Json("""{"slug":"caisse"}"""), "198.51.100.7", "Mozilla", default));

        status.Should().Be(StatusCodes.Status202Accepted);
        body.Should().Contain("CONFIRMATION_SENT");
        var mail = _mails.Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be("trial.signup-confirm");
        mail.TenantId.Should().Be(TrialSignupRelay.PlatformTenantId);
        mail.Locale.Should().Be("en");
        mail.IdempotencyKey.Should().Be("trial.signup-confirm:1");
        mail.TemplateData["confirm_url"].Should().Be("https://talisoft.net/en/trial/confirmation#t=x");
    }

    [Fact]
    public async Task A_refusal_is_relayed_with_its_status_and_mails_nobody()
    {
        _registry.ConfirmForPublicAsync("abc", Arg.Any<CancellationToken>())
            .Returns(new RegistryCall<RegistryPublicAnswer>(new RegistryPublicAnswer(410,
                Json("""{"error":"TRIAL_TOKEN_EXPIRED","detail":"Ce lien a expiré."}"""), null), null));

        var (status, body) = await RunAsync(await Relay().ConfirmAsync("abc", default));

        status.Should().Be(StatusCodes.Status410Gone);
        body.Should().Contain("TRIAL_TOKEN_EXPIRED");
        _mails.Should().BeEmpty();
    }

    [Fact]
    public async Task A_registry_outage_is_a_503_never_a_success()
    {
        _registry.CheckSlugForPublicAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RegistryCall<RegistryPublicAnswer>(null, RegistryCall<RegistryPublicAnswer>.Unavailable));

        var (status, body) = await RunAsync(await Relay().CheckSlugAsync("caisse", default));

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain(TrialSignupRelay.UnavailableCode);
        _mails.Should().BeEmpty();
    }

    [Fact]
    public void The_visitor_address_is_cloudflares_report_when_present()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("172.68.1.1");
        TrialSignupRelay.ClientIp(http).Should().Be("172.68.1.1");

        http.Request.Headers["CF-Connecting-IP"] = "198.51.100.7";
        TrialSignupRelay.ClientIp(http).Should().Be("198.51.100.7");

        http.Request.Headers["CF-Connecting-IP"] = "pas une adresse";
        TrialSignupRelay.ClientIp(http).Should().Be("172.68.1.1");
    }

    // ── Decisions ───────────────────────────────────────────────────────────────────────────────

    private readonly IRootPasswordSetup _rootSetup = Substitute.For<IRootPasswordSetup>();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();

    private TrialSignupDecisionsJob Job()
    {
        var services = new ServiceCollection().AddSingleton(_rootSetup).BuildServiceProvider();
        return new TrialSignupDecisionsJob(_registry, services.GetRequiredService<IServiceScopeFactory>(), _tenants,
            _notifications, NullLogger<TrialSignupDecisionsJob>.Instance);
    }

    private void Feed(params RegistrySignupDecision[] decisions) =>
        _registry.DecisionsToNotifyAsync(Arg.Any<CancellationToken>())
            .Returns(new RegistryCall<IReadOnlyList<RegistrySignupDecision>>(decisions, null));

    [Fact]
    public async Task A_refusal_is_mailed_with_its_reason_and_marked_told()
    {
        var id = Guid.NewGuid();
        Feed(new RegistrySignupDecision(id, "Rejected", "Caisse Avenir", "Awa", "awa@caisse.ci", null, "Doublon", null,
            DateTimeOffset.UtcNow, "fr"));

        await Job().ExecuteAsync(default);

        var mail = _mails.Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be("trial.signup-rejected");
        mail.TemplateData["reason"].Should().Be("Doublon");
        await _registry.Received(1).MarkDecisionNotifiedAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_approval_waits_for_the_root_account_then_mails_its_password_link()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        Feed(new RegistrySignupDecision(id, "Approved", "Caisse Avenir", "Awa", "awa@caisse.ci",
            "https://caisse-avenir.talisoftcrm.com", null, tenantId, DateTimeOffset.UtcNow, "fr"));

        _rootSetup.IssueAsync(tenantId, Arg.Any<CancellationToken>()).Returns((RootPasswordSetupLink?)null);
        await Job().ExecuteAsync(default);
        _mails.Should().BeEmpty();
        await _registry.DidNotReceiveWithAnyArgs().MarkDecisionNotifiedAsync(default, default);

        _rootSetup.IssueAsync(tenantId, Arg.Any<CancellationToken>()).Returns(new RootPasswordSetupLink(
            Guid.NewGuid(), "awa@caisse.ci", "https://caisse-avenir.talisoftcrm.com/auth/account-activation?token=t",
            AlreadySignedIn: false, DateTimeOffset.UtcNow.AddDays(7)));
        await Job().ExecuteAsync(default);

        var mail = _mails.Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be("trial.ready");
        mail.TenantId.Should().Be(tenantId);
        mail.TemplateData["crm_url"].Should().Be("https://caisse-avenir.talisoftcrm.com");
        ((string)mail.TemplateData["activation_url"]).Should().Contain("/auth/account-activation");
        mail.TemplateData["expires_days"].Should().Be(7);
        await _registry.Received(1).MarkDecisionNotifiedAsync(id, Arg.Any<CancellationToken>());
    }
}
