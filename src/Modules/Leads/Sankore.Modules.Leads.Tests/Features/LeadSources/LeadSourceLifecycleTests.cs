namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class LeadSourceLifecycleTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private LeadSourceConfig BuildSource(IntegrationMode mode = IntegrationMode.Internal,
        SourceSettings? settings = null, string? platformConnectionId = null)
        => LeadSourceConfig.Create(_tenantId, "TEST", "Test",
            LeadChannelType.WalkIn, 0, mode,
            settings: settings, platformConnectionId: platformConnectionId);

    // ── Valid transitions ────────────────────────────────────────────────

    [Fact]
    public void Draft_to_Testing_succeeds()
    {
        var source = BuildSource();
        source.StartTesting();
        source.Status.Should().Be(LeadSourceStatus.Testing);
    }

    [Fact]
    public void Testing_to_Active_succeeds_for_Internal_mode()
    {
        var source = BuildSource();
        source.StartTesting();
        var missing = source.Activate();
        missing.Should().BeEmpty();
        source.Status.Should().Be(LeadSourceStatus.Active);
    }

    [Fact]
    public void Active_to_Paused_succeeds()
    {
        var source = BuildSource();
        source.StartTesting();
        source.Activate();
        source.Pause();
        source.Status.Should().Be(LeadSourceStatus.Paused);
    }

    [Fact]
    public void Paused_to_Active_succeeds()
    {
        var source = BuildSource();
        source.StartTesting();
        source.Activate();
        source.Pause();
        var missing = source.Activate();
        missing.Should().BeEmpty();
        source.Status.Should().Be(LeadSourceStatus.Active);
    }

    [Fact]
    public void Active_to_Error_succeeds()
    {
        var source = BuildSource();
        source.StartTesting();
        source.Activate();
        source.MarkError();
        source.Status.Should().Be(LeadSourceStatus.Error);
    }

    [Fact]
    public void Error_to_Active_succeeds()
    {
        var source = BuildSource();
        source.StartTesting();
        source.Activate();
        source.MarkError();
        var missing = source.Activate();
        missing.Should().BeEmpty();
        source.Status.Should().Be(LeadSourceStatus.Active);
    }

    [Fact]
    public void Any_non_system_to_Archived_succeeds()
    {
        var source = BuildSource();
        source.Archive();
        source.Status.Should().Be(LeadSourceStatus.Archived);
    }

    [Fact]
    public void Active_to_Archived_succeeds()
    {
        var source = BuildSource();
        source.StartTesting();
        source.Activate();
        source.Archive();
        source.Status.Should().Be(LeadSourceStatus.Archived);
    }

    // ── Invalid transitions ─────────────────────────────────────────────

    [Fact]
    public void Draft_to_Active_is_rejected()
    {
        var source = BuildSource();
        var act = () => source.Activate();
        act.Should().Throw<DomainException>().WithMessage("INVALID_TRANSITION");
    }

    [Fact]
    public void Paused_to_Testing_is_rejected()
    {
        var source = BuildSource();
        source.StartTesting();
        source.Activate();
        source.Pause();
        var act = () => source.StartTesting();
        act.Should().Throw<DomainException>().WithMessage("INVALID_TRANSITION");
    }

    [Fact]
    public void Archived_to_Active_is_rejected()
    {
        var source = BuildSource();
        source.Archive();
        var act = () => source.Activate();
        act.Should().Throw<DomainException>().WithMessage("INVALID_TRANSITION");
    }

    [Fact]
    public void Archived_to_Archived_is_rejected()
    {
        var source = BuildSource();
        source.Archive();
        var act = () => source.Archive();
        act.Should().Throw<DomainException>().WithMessage("INVALID_TRANSITION");
    }

    // ── System source protection ────────────────────────────────────────

    [Fact]
    public void System_source_cannot_be_archived()
    {
        var source = LeadSourceConfig.CreateInternal(_tenantId, "SYS", "System", LeadChannelType.WalkIn, 0);
        var act = () => source.Archive();
        act.Should().Throw<DomainException>().WithMessage("CANNOT_ARCHIVE_SYSTEM_SOURCE");
    }

    // ── Activation prerequisites ────────────────────────────────────────

    [Fact]
    public void Activate_returns_SecretMissing_for_webhook_without_publickey()
    {
        // ServerWebhook normally auto-generates PublicKey, but let's test the check
        // by creating a PlatformConnection source missing credentials
        var settings = new PlatformSettings { PlatformName = "facebook" };
        var source = LeadSourceConfig.Create(_tenantId, "FB", "Facebook",
            LeadChannelType.FacebookLeadAds, 0, IntegrationMode.PlatformConnection,
            settings: settings);

        source.StartTesting();
        var missing = source.Activate();

        missing.Should().Contain("ConnectionNotConnected"); // no PlatformConnectionId
        missing.Should().Contain("SecretMissing");          // no OAuthCredentialVaultRef
    }

    [Fact]
    public void Activate_returns_NoFieldMappingConfigured_for_webhook_without_mapping()
    {
        var settings = new ServerWebhookSettings();
        var source = LeadSourceConfig.Create(_tenantId, "HOOK", "Hook",
            LeadChannelType.InboundWebhook, 0, IntegrationMode.ServerWebhook,
            settings: settings);

        source.StartTesting();
        var missing = source.Activate();

        missing.Should().Contain("NoFieldMappingConfigured");
    }

    [Fact]
    public void Activate_succeeds_when_all_prerequisites_met()
    {
        var settings = new ServerWebhookSettings
        {
            FieldMappings =
            [
                new() { SourceField = "$.phone", TargetField = "phoneNumber" },
                new() { SourceField = "$.name", TargetField = "fullName" }
            ]
        };
        var source = LeadSourceConfig.Create(_tenantId, "HOOK", "Hook",
            LeadChannelType.InboundWebhook, 0, IntegrationMode.ServerWebhook,
            settings: settings);

        source.StartTesting();
        var missing = source.Activate();

        missing.Should().BeEmpty();
        source.Status.Should().Be(LeadSourceStatus.Active);
    }

    // ── A pull endpoint may be missing while configuring, never once live ─

    private LeadSourceConfig BuildPullSource(string baseUrl)
        => LeadSourceConfig.Create(_tenantId, "PULL", "Lead Factory-pull",
            LeadChannelType.ExternalApiPull, 0, IntegrationMode.ScheduledPull,
            settings: PullSettings(baseUrl));

    private static ScheduledPullSettings PullSettings(string baseUrl) => new()
    {
        Pull = new PullConfig { BaseUrl = baseUrl },
        FieldMappings =
        [
            new() { SourceField = "$.data.phoneNumber", TargetField = "phoneNumber" },
            new() { SourceField = "$.data.name", TargetField = "fullName" }
        ]
    };

    [Fact]
    public void A_draft_pull_source_saves_without_its_endpoint()
    {
        var source = BuildPullSource(string.Empty);

        source.UpdateSettings(PullSettings(string.Empty));

        source.Settings.Should().BeOfType<ScheduledPullSettings>();
    }

    [Fact]
    public void A_pull_source_without_an_endpoint_cannot_be_activated()
    {
        var source = BuildPullSource(string.Empty);
        source.StartTesting();

        source.Activate().Should().Contain("NoEndpointConfigured");
        source.Status.Should().NotBe(LeadSourceStatus.Active);
    }

    [Fact]
    public void A_live_pull_source_refuses_to_have_its_endpoint_blanked()
    {
        var source = BuildPullSource("https://leads.example.com/v1/leads");
        source.StartTesting();
        source.Activate();
        source.Status.Should().Be(LeadSourceStatus.Active);

        // The schedule is running: an empty url would fail every run and escalate the source
        // to Error after three.
        var act = () => source.UpdateSettings(PullSettings(string.Empty));

        act.Should().Throw<DomainException>().WithMessage("LIVE_SOURCE_ENDPOINT_REQUIRED");
        source.Settings.Should().BeOfType<ScheduledPullSettings>()
            .Which.EndpointUrl.Should().Be("https://leads.example.com/v1/leads");
    }

    /// <summary>
    /// `authType: None` is an open provider API: nothing ever writes a vault reference for it,
    /// so a prerequisite demanding one was permanent — the source could reach Testing and never
    /// leave it, with "SecretMissing" naming a secret that does not exist.
    /// </summary>
    [Fact]
    public void An_unauthenticated_pull_source_needs_no_credential_to_activate()
    {
        var source = BuildPullSource("https://leads.example.com/v1/leads");
        source.StartTesting();

        source.Activate().Should().BeEmpty();
        source.Status.Should().Be(LeadSourceStatus.Active);
    }

    [Fact]
    public void An_authenticated_pull_source_still_needs_its_credential()
    {
        var settings = PullSettings("https://leads.example.com/v1/leads") with
        {
            Pull = new PullConfig
            {
                BaseUrl = "https://leads.example.com/v1/leads",
                AuthType = PullCredentialType.Bearer,
            },
        };
        var source = LeadSourceConfig.Create(_tenantId, "PULL", "Lead Factory-pull",
            LeadChannelType.ExternalApiPull, 0, IntegrationMode.ScheduledPull, settings: settings);
        source.StartTesting();

        source.Activate().Should().Contain("SecretMissing");
    }

    [Fact]
    public void Every_missing_pull_prerequisite_is_reported_at_once()
    {
        var settings = PullSettings(string.Empty) with
        {
            Pull = new PullConfig { BaseUrl = string.Empty, AuthType = PullCredentialType.Bearer },
        };
        var source = LeadSourceConfig.Create(_tenantId, "PULL", "Lead Factory-pull",
            LeadChannelType.ExternalApiPull, 0, IntegrationMode.ScheduledPull, settings: settings);
        source.StartTesting();

        // Not one at a time: a caller who fixes the endpoint must not then discover the secret.
        source.Activate().Should().Contain("NoEndpointConfigured").And.Contain("SecretMissing");
    }

    [Fact]
    public void A_live_pull_source_still_accepts_a_new_endpoint()
    {
        var source = BuildPullSource("https://leads.example.com/v1/leads");
        source.StartTesting();
        source.Activate();

        source.UpdateSettings(PullSettings("https://leads.example.com/v2/leads"));

        source.Settings.Should().BeOfType<ScheduledPullSettings>()
            .Which.EndpointUrl.Should().Be("https://leads.example.com/v2/leads");
    }
}
