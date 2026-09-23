namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class LeadSourceConfigTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    [Fact]
    public void Create_sets_status_to_Draft()
    {
        var source = LeadSourceConfig.Create(_tenantId, "WEB", "Web Form", LeadChannelType.WebForm, 0);
        source.Status.Should().Be(LeadSourceStatus.Draft);
    }

    [Fact]
    public void Create_auto_generates_PublicKey_for_EmbeddedScript()
    {
        var source = LeadSourceConfig.Create(_tenantId, "WEB", "Web Form",
            LeadChannelType.WebForm, 0, IntegrationMode.EmbeddedScript);

        source.PublicKey.Should().NotBeNullOrEmpty();
        // 32 bytes → base64url ~43 chars
        source.PublicKey!.Length.Should().BeGreaterThanOrEqualTo(40);
    }

    [Fact]
    public void Create_auto_generates_PublicKey_for_ServerWebhook()
    {
        var source = LeadSourceConfig.Create(_tenantId, "HOOK", "Webhook",
            LeadChannelType.InboundWebhook, 0, IntegrationMode.ServerWebhook);

        source.PublicKey.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Create_does_not_generate_PublicKey_for_Internal()
    {
        var source = LeadSourceConfig.Create(_tenantId, "WALK", "Walk-in",
            LeadChannelType.WalkIn, 0, IntegrationMode.Internal);

        source.PublicKey.Should().BeNull();
    }

    [Fact]
    public void Update_returns_changed_fields()
    {
        var source = LeadSourceConfig.Create(_tenantId, "WEB", "Original",
            LeadChannelType.WebForm, 0);

        var changed = source.Update(
            label: "Updated",
            description: "New desc",
            displayOrder: 5);

        changed.Should().Contain(nameof(LeadSourceConfig.Label));
        changed.Should().Contain(nameof(LeadSourceConfig.Description));
        changed.Should().Contain(nameof(LeadSourceConfig.DisplayOrder));
    }

    [Fact]
    public void Update_returns_empty_when_nothing_changed()
    {
        var source = LeadSourceConfig.Create(_tenantId, "WEB", "Label",
            LeadChannelType.WebForm, 0);

        var changed = source.Update(
            label: "Label",
            description: null,
            displayOrder: 0);

        changed.Should().BeEmpty();
    }

    [Fact]
    public void Update_rejects_PlatformConnectionId_change_on_Active_source()
    {
        var source = LeadSourceConfig.Create(_tenantId, "FB", "Facebook",
            LeadChannelType.FacebookLeadAds, 0, IntegrationMode.PlatformConnection,
            platformConnectionId: "conn-1");
        source.Activate();

        var act = () => source.Update(
            label: "Facebook",
            description: null,
            displayOrder: 0,
            platformConnectionId: "conn-2");

        act.Should().Throw<DomainException>()
            .WithMessage("*ACTIVE_SOURCE_IMMUTABLE_FIELDS*");
    }

    [Fact]
    public void Update_allows_PlatformConnectionId_change_on_Draft_source()
    {
        var source = LeadSourceConfig.Create(_tenantId, "FB", "Facebook",
            LeadChannelType.FacebookLeadAds, 0, IntegrationMode.PlatformConnection,
            platformConnectionId: "conn-1");

        source.Status.Should().Be(LeadSourceStatus.Draft);

        var changed = source.Update(
            label: "Facebook",
            description: null,
            displayOrder: 0,
            platformConnectionId: "conn-2");

        changed.Should().Contain(nameof(LeadSourceConfig.PlatformConnectionId));
    }

    [Fact]
    public void GeneratePublicKey_produces_valid_base64url()
    {
        var key = LeadSourceConfig.GeneratePublicKey();

        key.Should().NotContain("+");
        key.Should().NotContain("/");
        key.Should().NotContain("=");
        key.Length.Should().BeGreaterThanOrEqualTo(40);
    }
}
