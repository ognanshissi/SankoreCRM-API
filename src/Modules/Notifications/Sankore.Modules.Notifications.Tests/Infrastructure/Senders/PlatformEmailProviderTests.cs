namespace Sankore.Modules.Notifications.Tests.Infrastructure.Senders;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.Infrastructure.Providers;
using Sankore.Modules.Notifications.Infrastructure.Senders;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The platform account chosen at installation (<c>Notifications:Platform</c>): which sender a
/// tenant on the platform default reaches, which key it uses, who the mail is from, and what the
/// start-up refuses.
/// </summary>
public sealed class PlatformEmailProviderTests
{
    private static ResolvedEmailProvider PlatformDefault(bool hasCredential = false) => new(
        "Default", true, null, null, null, null, hasCredential, null, null, null, false, false);

    private static ResolvedEmailProvider TenantBrevo() => new(
        "Brevo", false, "noreply@mfi.ci", "MFI", null, null, true, null, null, null, false, false);

    private static PlatformEmailOptions Platform(string provider = "Brevo", string? key = "xkeysib-platform") => new()
    {
        Provider = provider,
        BrevoApiKey = key,
        FromEmail = "no-reply@sankore.ci",
        FromName = "Sankoré",
    };

    private sealed class RecordingSender : IEmailSender
    {
        public int Calls { get; private set; }

        public Task SendAsync(SendEmailRequest request, ResolvedEmailProvider provider, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static SendEmailRequest Message() => new(
        Guid.NewGuid(), Guid.NewGuid(), "a@b.ci", "A", null, "c@d.ci", null, "S", "<p>x</p>", null);

    // ── Routing ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Brevo", "brevo")]
    [InlineData("brevo", "brevo")]
    [InlineData("Smtp", "smtp")]
    public async Task A_tenant_on_the_platform_default_reaches_the_installed_provider(string installed, string expected)
    {
        var smtp = new RecordingSender();
        var brevo = new RecordingSender();
        var sp = new ServiceCollection()
            .AddKeyedSingleton<IEmailSender>("smtp", smtp)
            .AddKeyedSingleton<IEmailSender>("brevo", brevo)
            .BuildServiceProvider();

        var composite = new CompositeEmailSender(sp, Options.Create(Platform(installed)));
        await composite.SendAsync(Message(), PlatformDefault(), CancellationToken.None);

        (expected == "brevo" ? brevo : smtp).Calls.Should().Be(1);
        (expected == "brevo" ? smtp : brevo).Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_tenant_with_its_own_provider_is_not_rerouted_by_the_installation()
    {
        var smtp = new RecordingSender();
        var brevo = new RecordingSender();
        var sp = new ServiceCollection()
            .AddKeyedSingleton<IEmailSender>("smtp", smtp)
            .AddKeyedSingleton<IEmailSender>("brevo", brevo)
            .BuildServiceProvider();

        var composite = new CompositeEmailSender(sp, Options.Create(Platform("Smtp")));
        await composite.SendAsync(Message(), TenantBrevo(), CancellationToken.None);

        brevo.Calls.Should().Be(1);
        smtp.Calls.Should().Be(0);
    }

    // ── Which key ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_platform_default_uses_the_platform_key_never_a_key_left_in_the_tenant_vault()
    {
        var secrets = Substitute.For<ISecretsModule>();
        secrets.GetValueAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>()).Returns("xkeysib-tenant");
        var credentials = new VaultNotificationCredentials(secrets, Options.Create(Platform()));

        var key = await credentials.GetAsync(Guid.NewGuid(), PlatformDefault(hasCredential: true), CancellationToken.None);

        key.Should().Be("xkeysib-platform");
        await secrets.DidNotReceiveWithAnyArgs().GetValueAsync(default!, default);
    }

    [Fact]
    public async Task A_tenant_provider_still_reads_its_own_vault_entry()
    {
        var tenantId = Guid.NewGuid();
        var secrets = Substitute.For<ISecretsModule>();
        secrets.GetValueAsync(NotificationSecrets.CredentialKey(tenantId, "Brevo"), Arg.Any<CancellationToken>())
            .Returns("xkeysib-tenant");
        var credentials = new VaultNotificationCredentials(secrets, Options.Create(Platform()));

        var key = await credentials.GetAsync(tenantId, TenantBrevo(), CancellationToken.None);

        key.Should().Be("xkeysib-tenant");
    }

    // ── Who the mail is from ─────────────────────────────────────────────

    [Fact]
    public void The_platform_account_sends_as_the_platform_even_when_the_row_kept_a_tenant_address()
    {
        var stale = PlatformDefault() with { FromEmail = "old@mfi.ci", FromName = "MFI" };

        Platform().SenderFor(stale).Should().Be(("no-reply@sankore.ci", "Sankoré"));
    }

    [Fact]
    public void A_tenant_provider_sends_as_the_tenant_completed_by_the_platform()
    {
        var unnamed = TenantBrevo() with { FromName = null };

        Platform().SenderFor(unnamed).Should().Be(("noreply@mfi.ci", "Sankoré"));
    }

    // ── What the installation refuses ────────────────────────────────────

    private static PlatformEmailOptions Resolve(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var sp = new ServiceCollection().AddLogging().AddNotificationsModule(config).BuildServiceProvider();
        return sp.GetRequiredService<IOptions<PlatformEmailOptions>>().Value;
    }

    [Fact]
    public void Nothing_configured_keeps_smtp_and_the_documented_smtp_sender()
    {
        var options = Resolve(new()
        {
            ["Notifications:Smtp:FromEmail"] = "mail@mfi.ci",
            ["Notifications:Smtp:FromName"] = "MFI",
        });

        options.SenderKey.Should().Be("smtp");
        (options.FromEmail, options.FromName).Should().Be(("mail@mfi.ci", "MFI"));
    }

    [Fact]
    public void Brevo_fully_configured_is_accepted()
    {
        var options = Resolve(new()
        {
            ["Notifications:Platform:Provider"] = "Brevo",
            ["Notifications:Platform:BrevoApiKey"] = "xkeysib-platform",
            ["Notifications:Platform:FromEmail"] = "no-reply@sankore.ci",
        });

        options.SenderKey.Should().Be("brevo");
        options.FromEmail.Should().Be("no-reply@sankore.ci");
    }

    [Theory]
    [InlineData("Brevo", "", "no-reply@sankore.ci", "BrevoApiKey is empty")]
    [InlineData("Brevo", "xkeysib-platform", "", "FromEmail is empty")]
    [InlineData("Ses", "", "", "must be 'Smtp' or 'Brevo'")]
    public void A_half_configured_installation_is_refused(string provider, string key, string from, string expected)
    {
        var act = () => Resolve(new()
        {
            ["Notifications:Platform:Provider"] = provider,
            ["Notifications:Platform:BrevoApiKey"] = key,
            ["Notifications:Platform:FromEmail"] = from,
        });

        act.Should().Throw<OptionsValidationException>().WithMessage($"*{expected}*");
    }
}
