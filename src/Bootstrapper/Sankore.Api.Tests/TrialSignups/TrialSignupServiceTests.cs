namespace Sankore.Api.Tests.TrialSignups;

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Api.Features.TrialSignups;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The public side of the free trial (<c>docs/essai-gratuit-socle.md</c>, lot 2): what the website
/// may learn, which e-mails leave, and when a decision counts as told.
/// </summary>
public sealed class TrialSignupServiceTests
{
    private readonly ITrialSignupRegistry _registry = Substitute.For<ITrialSignupRegistry>();
    private readonly ITurnstileVerifier _captcha = Substitute.For<ITurnstileVerifier>();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly List<QueueEmailRequest> _mails = [];

    private readonly TrialSignupOptions _options = new()
    {
        ConfirmationUrl = "https://talisoft.net/essai/confirmation",
        ConfirmationUrlEn = "https://talisoft.net/en/trial/confirmation",
        PlatformRecipients = ["equipe@talisoft.net"],
    };

    public TrialSignupServiceTests()
    {
        _captcha.VerifyAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CaptchaOutcome.Passed);
        _notifications.QueueEmailAsync(Arg.Do<QueueEmailRequest>(_mails.Add), Arg.Any<CancellationToken>())
            .Returns(Result<Guid>.Ok(Guid.NewGuid()));
    }

    private TrialSignupService Service() =>
        new(_registry, _captcha, _notifications, Options.Create(_options), NullLogger<TrialSignupService>.Instance);

    private static TrialSignupForm Form(bool terms = true, string locale = "fr") => new(
        "Caisse Avenir", "caisse-avenir", "Awa", "Koné", "Awa.Kone@caisseavenir.ci", "+225 07 08 09 10 11", "CI",
        "Microfinance", terms, "captcha-token", locale);

    // ── Submission ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_request_mails_a_link_whose_token_only_the_mail_holds()
    {
        RegistrySignupRequest? sent = null;
        var signupId = Guid.NewGuid();
        _registry.CreateAsync(Arg.Do<RegistrySignupRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new RegistryCall<RegistrySignupCreated>(
                new RegistrySignupCreated(signupId, "https://caisse-avenir.talisoftcrm.com", DateTimeOffset.UtcNow), null));

        var result = await Service().SubmitAsync(Form(), "198.51.100.7", "Mozilla", default);

        result.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        result.Status.Should().Be(TrialPublicCodes.ConfirmationSent);

        var mail = _mails.Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be("trial.signup-confirm");
        mail.TenantId.Should().Be(TrialSignupService.PlatformTenantId);
        mail.IdempotencyKey.Should().Be($"trial.signup-confirm:{signupId}");
        var url = (string)mail.TemplateData["confirm_url"];
        url.Should().StartWith("https://talisoft.net/essai/confirmation#t=");

        // The registry got the HASH of the token in the link, never the token.
        var token = url[(url.IndexOf("#t=", StringComparison.Ordinal) + 3)..];
        sent!.TokenHash.Should().Be(TrialSignupService.HashOf(token)).And.NotContain(token);
        sent.RequestIp.Should().Be("198.51.100.7");
    }

    [Fact]
    public async Task The_english_page_gets_the_english_link_and_mail()
    {
        _registry.CreateAsync(Arg.Any<RegistrySignupRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RegistryCall<RegistrySignupCreated>(new RegistrySignupCreated(Guid.NewGuid(), "https://x.talisoftcrm.com", default), null));

        await Service().SubmitAsync(Form(locale: "en-GB"), null, null, default);

        var mail = _mails.Single();
        mail.Locale.Should().Be("en");
        ((string)mail.TemplateData["confirm_url"]).Should().StartWith("https://talisoft.net/en/trial/confirmation#t=");
    }

    [Fact]
    public async Task An_address_already_in_use_gets_the_same_answer_and_another_mail()
    {
        _registry.CreateAsync(Arg.Any<RegistrySignupRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RegistryCall<RegistrySignupCreated>(null, "TRIAL_EMAIL_IN_USE"));

        var result = await Service().SubmitAsync(Form(), null, null, default);

        result.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        result.Status.Should().Be(TrialPublicCodes.ConfirmationSent);
        result.Error.Should().BeNull();
        var mail = _mails.Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be("trial.signup-existing");
        mail.RecipientEmail.Should().Be("awa.kone@caisseavenir.ci");
        mail.TemplateData.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_captcha_writes_nothing_and_mails_nobody()
    {
        _captcha.VerifyAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(CaptchaOutcome.Failed);

        var result = await Service().SubmitAsync(Form(), null, null, default);

        result.Error.Should().Be(TrialPublicCodes.CaptchaFailed);
        await _registry.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
        _mails.Should().BeEmpty();
    }

    [Fact]
    public async Task Terms_not_accepted_stop_before_the_captcha_is_spent()
    {
        var result = await Service().SubmitAsync(Form(terms: false), null, null, default);

        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Errors.Should().ContainKey("acceptTerms");
        await _captcha.DidNotReceiveWithAnyArgs().VerifyAsync(default, default, default);
    }

    [Fact]
    public async Task A_taken_name_is_said_on_the_field()
    {
        _registry.CreateAsync(Arg.Any<RegistrySignupRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RegistryCall<RegistrySignupCreated>(null, TrialSlug.Taken, "Cette adresse est déjà prise."));

        var result = await Service().SubmitAsync(Form(), null, null, default);

        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        result.Errors.Should().ContainKey("slug");
        _mails.Should().BeEmpty();
    }

    [Fact]
    public async Task No_confirmation_page_configured_closes_the_form()
    {
        _options.ConfirmationUrl = "";
        var result = await Service().SubmitAsync(Form(), null, null, default);
        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        result.Error.Should().Be(TrialPublicCodes.NotConfigured);
    }

    [Fact]
    public async Task A_registry_outage_is_a_503_never_a_success()
    {
        _registry.CreateAsync(Arg.Any<RegistrySignupRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RegistryCall<RegistrySignupCreated>(null, RegistryCall<RegistrySignupCreated>.Unavailable));

        var result = await Service().SubmitAsync(Form(), null, null, default);

        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        result.Error.Should().Be(TrialPublicCodes.Unavailable);
        _mails.Should().BeEmpty();
    }

    // ── Confirmation ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_team_hears_of_a_request_on_its_first_confirmation_only()
    {
        var confirmed = new RegistrySignupConfirmed(Guid.NewGuid(), "Caisse Avenir", "awa@caisse.ci", NewlyConfirmed: true,
            "Awa", "Koné", "+225 07", "CI", "Microfinance", "caisse-avenir", "fr");
        _registry.ConfirmAsync(TrialSignupService.HashOf("abc"), Arg.Any<CancellationToken>())
            .Returns(new RegistryCall<RegistrySignupConfirmed>(confirmed, null),
                new RegistryCall<RegistrySignupConfirmed>(confirmed with { NewlyConfirmed = false }, null));

        (await Service().ConfirmAsync("abc", default)).Status.Should().Be(TrialPublicCodes.Confirmed);
        (await Service().ConfirmAsync(" abc ", default)).Status.Should().Be(TrialPublicCodes.Confirmed);

        var mail = _mails.Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be("trial.signup-received");
        mail.RecipientEmail.Should().Be("equipe@talisoft.net");
        mail.TemplateData["full_name"].Should().Be("Awa Koné");
    }

    [Fact]
    public async Task An_expired_link_says_so()
    {
        _registry.ConfirmAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RegistryCall<RegistrySignupConfirmed>(null, "TRIAL_TOKEN_EXPIRED"));

        var result = await Service().ConfirmAsync("abc", default);

        result.StatusCode.Should().Be(StatusCodes.Status410Gone);
        _mails.Should().BeEmpty();
    }

    [Fact]
    public void Tokens_are_url_safe_and_distinct()
    {
        var a = TrialSignupService.NewToken();
        var b = TrialSignupService.NewToken();
        a.Should().NotBe(b).And.MatchRegex("^[A-Za-z0-9_-]{43}$");
        TrialSignupService.HashOf(a).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void The_visitor_address_is_cloudflares_report_when_present()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("172.68.1.1");
        TrialSignupService.ClientIp(http).Should().Be("172.68.1.1");

        http.Request.Headers["CF-Connecting-IP"] = "198.51.100.7";
        TrialSignupService.ClientIp(http).Should().Be("198.51.100.7");

        http.Request.Headers["CF-Connecting-IP"] = "pas une adresse";
        TrialSignupService.ClientIp(http).Should().Be("172.68.1.1");
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
