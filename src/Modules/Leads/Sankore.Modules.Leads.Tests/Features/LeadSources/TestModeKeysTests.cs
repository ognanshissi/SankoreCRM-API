namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.RotatePublicKey;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Sandbox lot 3: a source's public key — in the snippet, the hosted form, the webhook URL — says
/// which mode it feeds, <c>pk_live_</c> or <c>pk_test_</c>, at creation and at every rotation.
/// </summary>
public sealed class TestModeKeysTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();

    public TestModeKeysTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private void Mode(bool test)
        => _tenants.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(_tenantId, "Caisse", "caisse.ci", true, false, null, null, IsTest: test));

    [Theory]
    [InlineData(false, TestModeKeys.LivePublicKey)]
    [InlineData(true, TestModeKeys.TestPublicKey)]
    public async Task A_new_source_and_a_rotated_key_carry_the_tenants_mode(bool test, string prefix)
    {
        Mode(test);
        await using var db = _factory.CreateContext();

        var created = await new CreateLeadSourceHandler(db, _publisher, tenants: _tenants).Handle(new CreateLeadSourceCommand(
            TenantId: _tenantId, Code: "WEB", Label: "Site", ChannelType: LeadChannelType.WebForm, DisplayOrder: 0), default);
        db.LeadSourceConfigs.Single(s => s.Id == created.Value).PublicKey.Should().StartWith(prefix);

        var rotated = await new RotatePublicKeyHandler(db, _publisher, _tenants).Handle(new RotatePublicKeyCommand(created.Value), default);
        rotated.Value.Should().StartWith(prefix);
    }

    [Fact]
    public void Without_a_registry_a_key_is_live()
        => LeadSourceConfig.Create(_tenantId, "WEB", "Site", LeadChannelType.WebForm, 0).PublicKey
            .Should().StartWith(TestModeKeys.LivePublicKey);

    [Theory]
    [InlineData("pk_test_abcdefghijklmnop", "pk_test_****mnop")]
    [InlineData("whsec_test_abcdefghijklmnop", "whsec_test_****mnop")]
    [InlineData("whsec_abcdefghijklmnop", "whsec_****mnop")]
    // Anything else masks as the vault always did.
    [InlineData("motdepasse-smtp-2026", "motd****2026")]
    [InlineData("court", "****")]
    public void A_mask_keeps_the_mode_prefix_in_full(string value, string masked)
        => TestModeKeys.Mask(value).Should().Be(masked);
}
