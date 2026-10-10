namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using FluentValidation.Results;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Connections.CreateConnection;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// Per-kind settings validation (INT-03, criterion 2).
///
/// <para>
/// Every assertion checks the FAILURE KEY and not only that validation failed. The keys are
/// <c>settings.&lt;camelCaseProp&gt;</c> on purpose: they are the path the field has in the
/// request JSON, which is what lets a front-end put the message under the right input instead of
/// in a toast.
/// </para>
/// </summary>
public sealed class CreateConnectionValidatorTests
{
    private readonly CreateConnectionValidator _validator = new();

    private ValidationResult Validate(
        IntegrationKind kind,
        ConnectionSettings? settings,
        IntegrationMode mode = IntegrationMode.Api,
        IntegrationFamily family = IntegrationFamily.CoreBanking,
        string name = "Connexion")
        => _validator.Validate(new CreateConnectionCommand(family, kind, mode, name, settings));

    private static IEnumerable<string> Keys(ValidationResult result)
        => result.Errors.Select(e => e.PropertyName);

    [Fact]
    public void A_complete_temenos_connection_is_valid()
    {
        var result = Validate(
            IntegrationKind.Temenos,
            new TemenosSettings
            {
                BaseUrl = "https://cbs.example.ci/api/",
                AuthMode = TemenosAuthMode.OAuthClientCredentials,
                TokenEndpoint = "https://cbs.example.ci/oauth/token",
                OAuthClientId = "sankore",
            });

        result.IsValid.Should().BeTrue(string.Join(", ", Keys(result)));
    }

    [Fact]
    public void A_missing_name_is_refused()
    {
        var result = Validate(IntegrationKind.Temenos, new TemenosSettings { BaseUrl = "https://x.ci/" }, name: "  ");

        Keys(result).Should().Contain("name");
    }

    [Fact]
    public void Missing_settings_are_refused_with_a_message_naming_the_discriminator()
    {
        var result = Validate(IntegrationKind.Temenos, settings: null);

        Keys(result).Should().Contain("settings");

        // The host's converter answers null for a settings object whose "$kind" is missing as
        // well as for no object at all, so the message has to mention it — otherwise a caller who
        // sent a full Temenos object without "$kind" reads "settings is required".
        result.Errors.Single(e => e.PropertyName == "settings")
            .ErrorMessage.Should().Contain("$kind");
    }

    [Fact]
    public void Settings_belonging_to_another_kind_are_refused()
    {
        var result = Validate(IntegrationKind.Temenos, new AmplitudeSettings());

        Keys(result).Should().Contain("settings");
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Amplitude"));
    }

    // ── Temenos ───────────────────────────────────────────────────────────

    [Fact]
    public void Temenos_requires_a_base_url()
    {
        var result = Validate(IntegrationKind.Temenos, new TemenosSettings());

        Keys(result).Should().Contain("settings.baseUrl");
    }

    [Theory]
    [InlineData("http://cbs.example.ci/api/")]
    [InlineData("cbs.example.ci")]
    [InlineData("ftp://cbs.example.ci/")]
    public void Temenos_refuses_a_base_url_that_is_not_absolute_https(string url)
    {
        var result = Validate(IntegrationKind.Temenos, new TemenosSettings { BaseUrl = url });

        // https even on-premise: a plain-http base URL would carry a bearer token across the
        // IMF's own LAN.
        Keys(result).Should().Contain("settings.baseUrl");
    }

    [Fact]
    public void Temenos_oauth_requires_the_token_endpoint_and_the_client_id()
    {
        var result = Validate(
            IntegrationKind.Temenos,
            new TemenosSettings
            {
                BaseUrl = "https://cbs.example.ci/api/",
                AuthMode = TemenosAuthMode.OAuthClientCredentials,
            });

        Keys(result).Should().Contain("settings.tokenEndpoint");
        Keys(result).Should().Contain("settings.oAuthClientId");
    }

    [Fact]
    public void Temenos_with_a_static_token_needs_neither_of_them()
    {
        var result = Validate(
            IntegrationKind.Temenos,
            new TemenosSettings
            {
                BaseUrl = "https://cbs.example.ci/api/",
                AuthMode = TemenosAuthMode.StaticToken,
            });

        // The token itself is in the vault, not here.
        result.IsValid.Should().BeTrue(string.Join(", ", Keys(result)));
    }

    [Fact]
    public void Temenos_refuses_a_token_endpoint_that_is_not_https()
    {
        var result = Validate(
            IntegrationKind.Temenos,
            new TemenosSettings
            {
                BaseUrl = "https://cbs.example.ci/api/",
                AuthMode = TemenosAuthMode.OAuthClientCredentials,
                TokenEndpoint = "http://cbs.example.ci/oauth/token",
                OAuthClientId = "sankore",
            });

        Keys(result).Should().Contain("settings.tokenEndpoint");
    }

    // ── Amplitude ─────────────────────────────────────────────────────────

    [Fact]
    public void Amplitude_up_requires_a_base_url()
    {
        var result = Validate(
            IntegrationKind.Amplitude,
            new AmplitudeSettings { AmplitudeVersion = AmplitudeVersion.Up });

        Keys(result).Should().Contain("settings.baseUrl");
    }

    [Fact]
    public void Legacy_amplitude_needs_none_because_it_has_no_api_at_all()
    {
        var result = Validate(
            IntegrationKind.Amplitude,
            new AmplitudeSettings { AmplitudeVersion = AmplitudeVersion.Legacy });

        result.IsValid.Should().BeTrue(string.Join(", ", Keys(result)));
    }

    // ── SAB ───────────────────────────────────────────────────────────────

    [Fact]
    public void Sab_requires_a_base_url_and_an_entity()
    {
        var result = Validate(IntegrationKind.Sab, new SabSettings());

        Keys(result).Should().Contain("settings.baseUrl");

        // On a multi-IMF network such as CIF, a call that does not say which entity lands on
        // whichever one Open SAB defaults to.
        Keys(result).Should().Contain("settings.entity");
    }

    [Fact]
    public void A_complete_sab_connection_is_valid()
    {
        var result = Validate(
            IntegrationKind.Sab,
            new SabSettings { BaseUrl = "https://opensab.example.ci/", Entity = "CIF01" });

        result.IsValid.Should().BeTrue(string.Join(", ", Keys(result)));
    }

    // ── Batch-capable kinds ───────────────────────────────────────────────

    [Fact]
    public void Batch_mode_requires_the_sftp_host_and_both_directories()
    {
        var result = Validate(
            IntegrationKind.PerfectVision, new PerfectVisionSettings(), IntegrationMode.Batch);

        Keys(result).Should().Contain("settings.sftpHost");
        Keys(result).Should().Contain("settings.outboundDirectory");

        // Both, always: depositing without polling produces files nobody acknowledges, and every
        // Batched command then waits out its ackTimeoutHours before alerting.
        Keys(result).Should().Contain("settings.inboundDirectory");
    }

    [Fact]
    public void A_complete_batch_connection_is_valid()
    {
        var result = Validate(
            IntegrationKind.PerfectVision,
            new PerfectVisionSettings
            {
                SftpHost = "sftp.imf.local",
                OutboundDirectory = "/out",
                InboundDirectory = "/in",
            },
            IntegrationMode.Batch);

        result.IsValid.Should().BeTrue(string.Join(", ", Keys(result)));
    }

    [Fact]
    public void A_relay_batch_capable_connection_needs_no_sftp_coordinates()
    {
        // The agent on the IMF's side owns the file system; SANKORE opens no SFTP session.
        var result = Validate(
            IntegrationKind.PerfectVision, new PerfectVisionSettings(), IntegrationMode.Relay);

        result.IsValid.Should().BeTrue(string.Join(", ", Keys(result)));
    }

    [Fact]
    public void Batch_mode_is_refused_for_a_kind_whose_settings_have_no_files()
    {
        var result = Validate(
            IntegrationKind.Temenos,
            new TemenosSettings { BaseUrl = "https://cbs.example.ci/api/" },
            IntegrationMode.Batch);

        Keys(result).Should().Contain("mode");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void An_impossible_sftp_port_is_refused(int port)
    {
        var result = Validate(
            IntegrationKind.Orass,
            new OrassSettings { SftpPort = port },
            IntegrationMode.Api,
            IntegrationFamily.Insurance);

        Keys(result).Should().Contain("settings.sftpPort");
    }

    // ── Shared knobs ──────────────────────────────────────────────────────

    [Fact]
    public void A_timeout_above_five_minutes_is_refused()
    {
        var result = Validate(
            IntegrationKind.Temenos,
            new TemenosSettings { BaseUrl = "https://cbs.example.ci/api/", TimeoutSeconds = 600 });

        // Above five minutes it is not a timeout, it is a hung request holding a pooled
        // connection for the dispatcher's whole cycle.
        Keys(result).Should().Contain("settings.timeoutSeconds");
    }

    [Fact]
    public void A_negative_rate_limit_is_refused()
    {
        var result = Validate(
            IntegrationKind.Temenos,
            new TemenosSettings { BaseUrl = "https://cbs.example.ci/api/", RateLimitPerMinute = -1 });

        Keys(result).Should().Contain("settings.rateLimitPerMinute");
    }

    [Fact]
    public void Zero_means_unlimited_and_is_accepted()
    {
        // Complete on every OTHER axis on purpose: the assertion is about the rate limit, and a
        // fixture missing the OAuth coordinates would pass or fail for an unrelated reason.
        var result = Validate(
            IntegrationKind.Temenos,
            new TemenosSettings
            {
                BaseUrl = "https://cbs.example.ci/api/",
                TokenEndpoint = "https://cbs.example.ci/oauth/token",
                OAuthClientId = "sankore",
                RateLimitPerMinute = 0,
            });

        result.IsValid.Should().BeTrue(string.Join(", ", Keys(result)));
    }
}
