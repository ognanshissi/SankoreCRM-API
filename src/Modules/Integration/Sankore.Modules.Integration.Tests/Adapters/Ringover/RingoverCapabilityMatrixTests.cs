namespace Sankore.Modules.Integration.Tests.Adapters.Ringover;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Ringover;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// The capability matrix — the one positive claim this assembly makes, and the one a screen reads
/// before anything is unblocked (TEL-B-04).
/// </summary>
public sealed class RingoverCapabilityMatrixTests
{
    [Fact]
    public void A_configured_connection_declares_the_five_telephony_capabilities()
    {
        var caps = RingoverCapabilityMatrix.For(RingoverHarness.ConfiguredSettings());

        caps.Supports(IntegrationCapability.ClickToCall).Should().BeTrue();
        caps.Supports(IntegrationCapability.CallEvents).Should().BeTrue();
        caps.Supports(IntegrationCapability.CallHistory).Should().BeTrue();
        caps.Supports(IntegrationCapability.Recordings).Should().BeTrue();
        caps.Supports(IntegrationCapability.UserDirectory).Should().BeTrue();
    }

    [Fact]
    public void RecordingDeletion_is_NOT_declared_and_that_is_the_interesting_entry()
    {
        var caps = RingoverCapabilityMatrix.For(RingoverHarness.ConfiguredSettings());

        // Open question 6, with the spec's fallback applied: « purge côté SANKORE seule ». A
        // capability declared on a guess would be WORSE than the fallback rather than better — the
        // retention job would call DeleteRecordingAsync on every expiring recording, every failure
        // would be a row somebody has to triage, and a compliance officer reading the capability
        // list would believe the provider's copy was being destroyed.
        caps.Supports(IntegrationCapability.RecordingDeletion).Should().BeFalse();

        // And fetching is not the same claim as deleting: a provider can have the first and not
        // the second.
        caps.Supports(IntegrationCapability.Recordings).Should().BeTrue();
    }

    [Fact]
    public void Every_capability_is_declared_REAL_TIME_because_the_family_has_no_batch_carrier()
    {
        var caps = RingoverCapabilityMatrix.For(RingoverHarness.ConfiguredSettings());

        foreach (var (capability, mode) in caps.Modes)
        {
            // Declaring Batch would have the platform wait for an acknowledgement nothing ever
            // produces, for an operation whose whole point is that an agent is holding a handset.
            mode.Should().Be(CapabilityMode.RealTime, $"{capability} is served live");
        }
    }

    [Fact]
    public void A_connection_with_no_API_host_promises_NOTHING()
    {
        // The provider serves regions on distinct hosts, so there is no default that is right for
        // everybody — and a tenant whose data must stay in one region cannot be pointed at
        // another. A row without it is a row this adapter promises nothing about, which is also
        // what ConnectionSettingsValidator refuses to let an administrator save.
        RingoverCapabilityMatrix.For(RingoverHarness.HostlessSettings())
            .Modes.Should().BeEmpty();

        RingoverCapabilityMatrix.For(RingoverHarness.HostlessSettings("   "))
            .Modes.Should().BeEmpty();
    }

    [Fact]
    public void Settings_of_another_kind_answer_NONE_rather_than_throwing()
    {
        // A screen asking what is available must not fail over a row somebody has to correct.
        RingoverCapabilityMatrix.For(null).Modes.Should().BeEmpty();
    }

    [Fact]
    public void The_adapter_reports_the_matrix_of_the_connection_it_is_HANDED()
    {
        using var harness = RingoverHarness.With(RingoverHarness.ConfiguredSettings());

        var configured = harness.Adapter.CapabilitiesFor(
            RingoverHarness.ConnectionCarrying(RingoverHarness.ConfiguredSettings()));

        var hostless = harness.Adapter.CapabilitiesFor(
            RingoverHarness.ConnectionCarrying(RingoverHarness.HostlessSettings()));

        // Per row and not per tenant: CapabilitiesFor reads its input off the connection and
        // queries nothing, so two rows of one tenant can answer differently.
        configured.Modes.Should().NotBeEmpty();
        hostless.Modes.Should().BeEmpty();
    }

    [Fact]
    public void A_Ringover_row_carrying_another_kinds_settings_narrows_to_NONE()
    {
        using var harness = RingoverHarness.With(RingoverHarness.ConfiguredSettings());

        var foreign = harness.Adapter.CapabilitiesFor(
            RingoverHarness.ConnectionCarrying(new FakeSettings()));

        foreign.Modes.Should().BeEmpty();
    }
}
