namespace Sankore.Admin.Tests.TenantSignups;

using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.TenantSignups;
using Sankore.Admin.Infrastructure;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The website's free-trial form, decided by the registry and relayed by Sankore.Api: what the public
/// may learn, which e-mails the API is asked to queue, and that the token never reaches a column.
/// </summary>
public sealed class TrialPublicServiceTests : IDisposable
{
    private readonly string _name = Guid.NewGuid().ToString();
    private readonly FakeCaptcha _captcha = new();
    private readonly TenantSignupOptions _options = new()
    {
        AddressTemplate = "https://{slug}.talisoftcrm.com",
        ConfirmationUrl = "https://talisoft.net/essai/confirmation",
        ConfirmationUrlEn = "https://talisoft.net/en/trial/confirmation",
        PlatformRecipients = ["equipe@talisoft.net"],
    };

    private readonly ServiceProvider _services;

    public TrialPublicServiceTests()
    {
        _services = new ServiceCollection()
            .AddDbContext<AdminDbContext>(o => o.UseInMemoryDatabase(_name))
            .AddSingleton(Options.Create(_options))
            .AddSingleton(TimeProvider.System)
            .AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(TenantSignupOptions).Assembly))
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        using (var scope = _services.CreateScope())
            scope.ServiceProvider.GetRequiredService<AdminDbContext>().Database.EnsureDeleted();
        _services.Dispose();
    }

    private async Task<TrialPublicAnswer> RunAsync(Func<TrialPublicService, Task<TrialPublicAnswer>> act)
    {
        await using var scope = _services.CreateAsyncScope();
        var service = new TrialPublicService(scope.ServiceProvider.GetRequiredService<ISender>(), _captcha,
            Options.Create(_options), NullLogger<TrialPublicService>.Instance);
        return await act(service);
    }

    private static TrialSignupForm Form(bool terms = true, string locale = "fr", string slug = "caisse-avenir") => new(
        "Caisse Avenir", slug, "Awa", "Koné", "Awa.Kone@caisseavenir.ci", "+225 07 08 09 10 11", "CI",
        "Microfinance", terms, "captcha-token", locale);

    private static JsonElement BodyOf(TrialPublicAnswer answer) => JsonSerializer.SerializeToElement(answer.Body);

    private static string? Read(TrialPublicAnswer answer, string property) =>
        BodyOf(answer).TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ── Submission ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_request_asks_for_a_link_whose_token_only_the_mail_holds()
    {
        var answer = await RunAsync(s => s.SubmitAsync(Form(), "198.51.100.7", "Mozilla", default));

        answer.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        Read(answer, "status").Should().Be(TrialPublicCodes.ConfirmationSent);

        var mail = answer.Mails.Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be("trial.signup-confirm");
        mail.Data["crm_address"].Should().Be("https://caisse-avenir.talisoftcrm.com");
        mail.Data["expires_hours"].Should().Be("48");
        var url = mail.Data["confirm_url"];
        url.Should().StartWith("https://talisoft.net/essai/confirmation#t=");

        // The registry stored the HASH of the token in the link, never the token.
        var token = url[(url.IndexOf("#t=", StringComparison.Ordinal) + 3)..];
        await using var scope = _services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AdminDbContext>().TenantSignups.SingleAsync();
        stored.TokenHash.Should().Be(TrialPublicService.HashOf(token)).And.NotContain(token);
        stored.RequestIp.Should().Be("198.51.100.7");
        mail.IdempotencyKey.Should().Be($"trial.signup-confirm:{stored.Id}");
    }

    [Fact]
    public async Task The_english_page_gets_the_english_link_and_mail()
    {
        var answer = await RunAsync(s => s.SubmitAsync(Form(locale: "en-GB"), null, null, default));

        var mail = answer.Mails.Single();
        mail.Locale.Should().Be("en");
        mail.Data["confirm_url"].Should().StartWith("https://talisoft.net/en/trial/confirmation#t=");
    }

    [Fact]
    public async Task An_address_already_in_use_gets_the_same_answer_and_another_mail()
    {
        await RunAsync(s => s.SubmitAsync(Form(), null, null, default));
        var answer = await RunAsync(s => s.SubmitAsync(Form(slug: "autre-caisse"), null, null, default));

        answer.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        Read(answer, "status").Should().Be(TrialPublicCodes.ConfirmationSent);
        Read(answer, "error").Should().BeNull();
        var mail = answer.Mails.Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be("trial.signup-existing");
        mail.To.Should().Be("awa.kone@caisseavenir.ci");
        mail.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_captcha_writes_nothing_and_mails_nobody()
    {
        _captcha.Outcome = CaptchaOutcome.Failed;

        var answer = await RunAsync(s => s.SubmitAsync(Form(), null, null, default));

        Read(answer, "error").Should().Be(TrialPublicCodes.CaptchaFailed);
        answer.Mails.Should().BeEmpty();
        await using var scope = _services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AdminDbContext>().TenantSignups.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Terms_not_accepted_stop_before_the_captcha_is_spent()
    {
        var answer = await RunAsync(s => s.SubmitAsync(Form(terms: false), null, null, default));

        answer.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        BodyOf(answer).GetProperty("errors").TryGetProperty("acceptTerms", out _).Should().BeTrue();
        _captcha.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_taken_name_is_said_on_the_field()
    {
        await RunAsync(s => s.SubmitAsync(Form(), null, null, default));
        var other = Form() with { Email = "other@caisse.ci" };

        var answer = await RunAsync(s => s.SubmitAsync(other, null, null, default));

        answer.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        Read(answer, "error").Should().Be(TrialSlug.Taken);
        BodyOf(answer).GetProperty("errors").TryGetProperty("slug", out _).Should().BeTrue();
        answer.Mails.Should().BeEmpty();
    }

    [Fact]
    public async Task No_confirmation_page_configured_closes_the_form()
    {
        _options.ConfirmationUrl = "";

        var answer = await RunAsync(s => s.SubmitAsync(Form(), null, null, default));

        answer.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        Read(answer, "error").Should().Be(TenantSignupErrors.NotConfigured);
        _captcha.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Availability_is_said_as_the_website_reads_it()
    {
        var answer = await RunAsync(s => s.CheckSlugAsync("caisse-avenir", default));

        answer.StatusCode.Should().Be(StatusCodes.Status200OK);
        Read(answer, "status").Should().Be(TrialSlugAvailabilityEndpoint.Available);
        Read(answer, "fqdn").Should().Be("https://caisse-avenir.talisoftcrm.com");
    }

    // ── Confirmation ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_team_hears_of_a_request_on_its_first_confirmation_only()
    {
        var submitted = await RunAsync(s => s.SubmitAsync(Form(), null, null, default));
        var url = submitted.Mails.Single().Data["confirm_url"];
        var token = url[(url.IndexOf("#t=", StringComparison.Ordinal) + 3)..];

        var first = await RunAsync(s => s.ConfirmAsync(token, default));
        var second = await RunAsync(s => s.ConfirmAsync($" {token} ", default));

        Read(first, "status").Should().Be(TrialPublicCodes.Confirmed);
        Read(second, "status").Should().Be(TrialPublicCodes.Confirmed);
        var mail = first.Mails.Should().ContainSingle().Subject;
        mail.TemplateKey.Should().Be("trial.signup-received");
        mail.To.Should().Be("equipe@talisoft.net");
        mail.Data["full_name"].Should().Be("Awa Koné");
        mail.Data["crm_address"].Should().Be("https://caisse-avenir.talisoftcrm.com");
        second.Mails.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_link_says_so()
    {
        var answer = await RunAsync(s => s.ConfirmAsync("abc", default));

        answer.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        Read(answer, "error").Should().Be(TenantSignupErrors.TokenUnknown);
        answer.Mails.Should().BeEmpty();
    }

    [Fact]
    public void Tokens_are_url_safe_and_distinct()
    {
        var a = TrialPublicService.NewToken();
        var b = TrialPublicService.NewToken();
        a.Should().NotBe(b).And.MatchRegex("^[A-Za-z0-9_-]{43}$");
        TrialPublicService.HashOf(a).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    private sealed class FakeCaptcha : ITurnstileVerifier
    {
        public CaptchaOutcome Outcome { get; set; } = CaptchaOutcome.Passed;
        public int Calls { get; private set; }

        public Task<CaptchaOutcome> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Outcome);
        }
    }
}
