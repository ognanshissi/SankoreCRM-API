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
}
