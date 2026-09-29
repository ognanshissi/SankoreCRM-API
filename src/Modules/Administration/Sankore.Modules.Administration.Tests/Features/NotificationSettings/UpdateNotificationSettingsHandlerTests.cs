namespace Sankore.Modules.Administration.Tests.Features.NotificationSettings;

using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.NotificationSettings.UpdateNotificationSettings;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The credential is the whole point of this handler: it must reach the vault and never a column.
/// </summary>
public sealed class UpdateNotificationSettingsHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly ICurrentUser _currentUser;

    /// <summary>In-memory stand-in for the vault, keyed exactly as the real one is.</summary>
    private readonly Dictionary<SecretKey, string> _vault = [];
    private readonly ISecretsModule _secrets = Substitute.For<ISecretsModule>();

    public UpdateNotificationSettingsHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        _currentUser = Substitute.For<ICurrentUser>();
        _currentUser.TenantId.Returns(_tenantId);
        _currentUser.Id.Returns(_userId);

        _secrets
            .When(s => s.SetAsync(Arg.Any<SecretKey>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()))
            .Do(call => _vault[(SecretKey)call[0]] = (string)call[1]);

        _secrets.GetHintAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns(call => _vault.ContainsKey((SecretKey)call[0])
                ? new SecretHint("credential", "****", null)
                : null);
    }

    public void Dispose() => _factory.Dispose();

    private UpdateNotificationSettingsHandler Handler() => new(
        _factory.CreateContext(),
        _currentUser,
        _secrets,
        Substitute.For<IEventPublisher>(),
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

    private static UpdateNotificationSettingsCommand Smtp(string? credential = "s3cret") =>
        new("Smtp", "noreply@mfi.ci", "MFI", null, null, credential,
            SmtpHost: "smtp.mfi.ci", SmtpPort: 587, SmtpUsername: "mfi",
            SmtpUseSsl: false, SmtpUseStartTls: true);

    private static UpdateNotificationSettingsCommand Brevo(string? credential = "xkeysib-abc") =>
        new("Brevo", "noreply@mfi.ci", "MFI", null, null, credential,
            SmtpHost: null, SmtpPort: null, SmtpUsername: null,
            SmtpUseSsl: false, SmtpUseStartTls: false);

    private async Task<TenantNotificationSettings> ReloadAsync()
    {
        await using var db = _factory.CreateContext();
        return db.TenantNotificationSettings.Single();
    }

    // ── the credential ──────────────────────────────────────────────────────

    [Fact]
    public async Task Writes_the_smtp_password_to_the_vault_and_never_to_a_column()
    {
        var result = await Handler().Handle(Smtp(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var key = NotificationSecrets.CredentialKey(_tenantId, "Smtp");
        _vault.Should().ContainKey(key);
        _vault[key].Should().Be("s3cret");

        var settings = await ReloadAsync();
        settings.HasCredential.Should().BeTrue();

        // Nothing on the row may carry the secret.
        System.Text.Json.JsonSerializer.Serialize(settings).Should().NotContain("s3cret");
    }

    [Fact]
    public async Task Stores_the_brevo_api_key_under_its_own_key()
    {
        await Handler().Handle(Brevo(), CancellationToken.None);

        _vault.Should().ContainKey(NotificationSecrets.CredentialKey(_tenantId, "Brevo"));
        _vault[NotificationSecrets.CredentialKey(_tenantId, "Brevo")].Should().Be("xkeysib-abc");
    }

    [Fact]
    public async Task Each_provider_keeps_its_own_credential()
    {
        // Switching to Brevo and back must not have destroyed the SMTP password.
        await Handler().Handle(Smtp("smtp-pass"), CancellationToken.None);
        await Handler().Handle(Brevo("brevo-key"), CancellationToken.None);

        _vault[NotificationSecrets.CredentialKey(_tenantId, "Smtp")].Should().Be("smtp-pass");
        _vault[NotificationSecrets.CredentialKey(_tenantId, "Brevo")].Should().Be("brevo-key");
    }

    [Fact]
    public async Task Omitting_the_credential_keeps_the_one_already_stored()
    {
        // An administrator changing only the From address should not have to re-type a password
        // they may not even have.
        await Handler().Handle(Smtp("s3cret"), CancellationToken.None);

        await Handler().Handle(
            Smtp(credential: null) with { FromName = "MFI Côte d'Ivoire" }, CancellationToken.None);

        _vault[NotificationSecrets.CredentialKey(_tenantId, "Smtp")].Should().Be("s3cret");
        (await ReloadAsync()).HasCredential.Should().BeTrue();
        (await ReloadAsync()).FromName.Should().Be("MFI Côte d'Ivoire");
    }

    [Fact]
    public async Task Switching_provider_without_a_credential_reports_none()
    {
        await Handler().Handle(Smtp("s3cret"), CancellationToken.None);

        await Handler().Handle(Brevo(credential: null), CancellationToken.None);

        var settings = await ReloadAsync();
        settings.ProviderType.Should().Be("Brevo");
        settings.HasCredential.Should().BeFalse(
            "the stored secret belongs to SMTP; Brevo has none until someone provides one");
    }

    // ── the relay settings ──────────────────────────────────────────────────

    [Fact]
    public async Task Records_the_tenant_relay()
    {
        await Handler().Handle(Smtp(), CancellationToken.None);

        var settings = await ReloadAsync();
        settings.SmtpHost.Should().Be("smtp.mfi.ci");
        settings.SmtpPort.Should().Be(587);
        settings.SmtpUsername.Should().Be("mfi");
        settings.SmtpUseStartTls.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, 465)]
    [InlineData(false, 587)]
    public async Task Derives_the_port_from_the_tls_mode_when_it_is_omitted(bool useSsl, int expected)
    {
        // 465 implicit TLS, 587 STARTTLS: nobody should have to remember that.
        var command = Smtp() with { SmtpPort = null, SmtpUseSsl = useSsl, SmtpUseStartTls = !useSsl };

        await Handler().Handle(command, CancellationToken.None);

        (await ReloadAsync()).SmtpPort.Should().Be(expected);
    }

    [Fact]
    public async Task Choosing_your_own_relay_does_not_mark_you_as_using_the_platform_account()
    {
        // The old rule had this exactly backwards.
        await Handler().Handle(Smtp(), CancellationToken.None);

        (await ReloadAsync()).UseDefaultPlatformProvider.Should().BeFalse();
    }

    [Fact]
    public async Task Only_default_means_the_platform_account()
    {
        var command = new UpdateNotificationSettingsCommand(
            "Default", null, null, null, null, null, null, null, null, false, false);

        await Handler().Handle(command, CancellationToken.None);

        (await ReloadAsync()).UseDefaultPlatformProvider.Should().BeTrue();
    }
}
