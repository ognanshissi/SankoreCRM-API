namespace Sankore.Modules.Leads.Tests.Features.Ingestion;

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Features.Ingestion.Web;
using Xunit;

/// <summary>
/// The captcha check of web leads. It replaced a stub that accepted every token (2026-11-14), so a
/// source « protected » by a captcha took anything; these pin that it now asks the provider and
/// refuses whenever it cannot say yes.
/// </summary>
public sealed class SiteVerifyCaptchaValidatorTests
{
    private sealed class Provider(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(Uri Url, string Form)> Calls { get; } = [];
        public bool Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw) throw new HttpRequestException("down");
            Calls.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private static SiteVerifyCaptchaValidator Validator(Provider provider, string environment = "Production")
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(SiteVerifyCaptchaValidator.HttpClientName).Returns(_ => new HttpClient(provider, disposeHandler: false));
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        return new SiteVerifyCaptchaValidator(factory, env, NullLogger<SiteVerifyCaptchaValidator>.Instance);
    }

    [Fact]
    public async Task A_token_the_provider_accepts_passes_and_the_secret_and_token_are_sent()
    {
        var provider = new Provider(HttpStatusCode.OK, """{"success":true}""");

        var outcome = await Validator(provider).ValidateAsync("Turnstile", "sec", "tok", "1.2.3.4", CancellationToken.None);

        outcome.Should().Be(CaptchaOutcome.Passed);
        provider.Calls.Should().ContainSingle();
        provider.Calls[0].Url.Host.Should().Be("challenges.cloudflare.com");
        provider.Calls[0].Form.Should().Contain("secret=sec").And.Contain("response=tok").And.Contain("remoteip=1.2.3.4");
    }

    [Fact]
    public async Task A_token_the_provider_refuses_fails()
    {
        var provider = new Provider(HttpStatusCode.OK, """{"success":false,"error-codes":["invalid-input-response"]}""");

        (await Validator(provider).ValidateAsync("HCaptcha", "sec", "tok", "1.2.3.4", CancellationToken.None))
            .Should().Be(CaptchaOutcome.Failed);
        provider.Calls[0].Url.Host.Should().Be("api.hcaptcha.com");
    }

    [Fact]
    public async Task A_low_recaptcha_score_fails_even_when_the_token_is_valid()
    {
        var provider = new Provider(HttpStatusCode.OK, """{"success":true,"score":0.2}""");

        (await Validator(provider).ValidateAsync("RecaptchaV3", "sec", "tok", "1.2.3.4", CancellationToken.None))
            .Should().Be(CaptchaOutcome.Failed);
    }

    [Fact]
    public async Task An_empty_token_fails_without_asking_the_provider()
    {
        var provider = new Provider(HttpStatusCode.OK, """{"success":true}""");

        (await Validator(provider).ValidateAsync("Turnstile", "sec", "", "1.2.3.4", CancellationToken.None))
            .Should().Be(CaptchaOutcome.Failed);
        provider.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreachable_provider_fails_rather_than_lets_the_lead_in()
    {
        var provider = new Provider(HttpStatusCode.OK, "{}") { Throw = true };

        (await Validator(provider).ValidateAsync("Turnstile", "sec", "tok", "1.2.3.4", CancellationToken.None))
            .Should().Be(CaptchaOutcome.Failed);
    }

    [Fact]
    public async Task Without_a_secret_the_lead_is_refused_outside_development_and_let_through_in_development()
    {
        var provider = new Provider(HttpStatusCode.OK, """{"success":true}""");

        (await Validator(provider).ValidateAsync("Turnstile", null, "tok", "1.2.3.4", CancellationToken.None))
            .Should().Be(CaptchaOutcome.NotConfigured);
        (await Validator(provider, Environments.Development).ValidateAsync("Turnstile", null, "tok", "1.2.3.4", CancellationToken.None))
            .Should().Be(CaptchaOutcome.Passed);
        provider.Calls.Should().BeEmpty();
    }
}
