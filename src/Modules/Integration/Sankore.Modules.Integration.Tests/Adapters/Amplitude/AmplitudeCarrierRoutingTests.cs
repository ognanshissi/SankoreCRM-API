namespace Sankore.Modules.Integration.Tests.Adapters.Amplitude;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Amplitude;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-31, criterion 2 — « sur les versions antérieures, il bascule sur le socle batch, selon
/// <c>AmplitudeSettings.AmplitudeVersion</c> ».
///
/// <para>
/// The branch is pinned on its own, with no adapter, no database and no tenant, because it is the
/// half of criterion 2 that does not wait on SBS: the decision is ours, the services and the
/// records are theirs. These tests keep passing unchanged the day the contract arrives, which is
/// the point — a routing rule that had to be re-asserted alongside a new client would be a rule
/// nobody trusts.
/// </para>
///
/// <para>
/// No Amplitude service name, endpoint, record or field appears anywhere in this chantier. The
/// conditions under test are an enum and a mode, and a plausible identifier would be the first
/// invented SBS name in the repository.
/// </para>
/// </summary>
public sealed class AmplitudeCarrierRoutingTests
{
    /// <summary>
    /// The six combinations, as one table.
    ///
    /// <para>
    /// A theory rather than six facts because the table IS the decision: a reviewer reads the rows
    /// side by side and sees that only the release decides whether an API exists, only the mode
    /// decides whether writes use it, and that exactly two rows are configuration mistakes.
    /// </para>
    /// </summary>
    public static TheoryData<AmplitudeVersion, IntegrationMode, AmplitudeCarrier, bool> Combinations()
        => new()
        {
            // Up on Api: the services exist and we call them.
            { AmplitudeVersion.Up, IntegrationMode.Api, AmplitudeCarrier.ApiServices, true },

            // Up on Relay: legitimate, and its WRITES still leave in a file. This row asserted
            // ApiServices until L8 and was wrong — the relay agent's file carrier is delivered and
            // its order channel is not, so the dispatcher sends every relay write with batch
            // coordinates to the socle, and AmplitudeSettings always has them. A matrix declaring
            // these writes real-time put a live button on a screen for a write that leaves at a
            // cut-off. The live READS are unaffected, which the read test below pins separately.
            { AmplitudeVersion.Up, IntegrationMode.Relay, AmplitudeCarrier.BatchSocle, true },

            // Up on a file-exchanging connection: legitimate, and a choice rather than a mistake —
            // the API module may not be licensed, or the institution's policy may forbid inbound
            // calls. The dispatcher diverts the commands before any adapter is resolved, so the
            // carrier IS the socle whatever the release can do.
            { AmplitudeVersion.Up, IntegrationMode.Batch, AmplitudeCarrier.BatchSocle, true },

            // Pre-Up: the only shape it has.
            { AmplitudeVersion.Legacy, IntegrationMode.Batch, AmplitudeCarrier.BatchSocle, true },

            // Pre-Up on Api: incoherent, and the only incoherent pair left. It would hand every
            // command to an adapter with no API to call, so the write would never move.
            { AmplitudeVersion.Legacy, IntegrationMode.Api, AmplitudeCarrier.BatchSocle, false },

            // Pre-Up on Relay: COHERENT since L8, and this row asserted the opposite. It was
            // incoherent only because the outbound batch job scanned Mode == Batch alone, so a
            // relay connection produced no file; the dispatcher and the scheduled generation now
            // read one definition (OutboundBatchCarrier) and both see it. A pre-Up installation
            // whose SFTP server sits inside the institution's network is exactly this shape.
            { AmplitudeVersion.Legacy, IntegrationMode.Relay, AmplitudeCarrier.BatchSocle, true },
        };

    [Theory]
    [MemberData(nameof(Combinations))]
    public void The_release_and_the_mode_together_decide_the_carrier(
        AmplitudeVersion version,
        IntegrationMode mode,
        AmplitudeCarrier expected,
        bool coherent)
    {
        var settings = new AmplitudeSettings { AmplitudeVersion = version };

        AmplitudeCarrierRouting.ChooseFor(settings, mode).Should().Be(expected);
        AmplitudeCarrierRouting.IsModeCoherent(settings, mode).Should().Be(coherent);
    }

    [Fact]
    public void An_incoherent_configuration_still_resolves_to_the_narrow_carrier()
    {
        // It never throws and never answers ApiServices. The incoherence is reported through the
        // health check, where an administrator can act on it; a carrier decision that threw would
        // make a capability matrix — which a screen merely asks for — fail instead.
        var settings = new AmplitudeSettings { AmplitudeVersion = AmplitudeVersion.Legacy };

        AmplitudeCarrierRouting.ChooseFor(settings, IntegrationMode.Api)
            .Should().Be(AmplitudeCarrier.BatchSocle);
    }

    [Theory]
    [InlineData(AmplitudeVersion.Up, true)]
    [InlineData(AmplitudeVersion.Legacy, false)]
    public void Only_an_up_installation_serves_a_read_live(AmplitudeVersion version, bool expected)
    {
        AmplitudeCarrierRouting
            .ServesApiReads(new AmplitudeSettings { AmplitudeVersion = version })
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(IntegrationMode.Api)]
    [InlineData(IntegrationMode.Batch)]
    [InlineData(IntegrationMode.Relay)]
    public void The_mode_does_not_decide_whether_a_read_is_served_live(IntegrationMode mode)
    {
        // The asymmetry with the writes, and it is deliberate rather than an oversight: the read
        // path (facade → resolver → adapter) never consults the mode, and Perfect Vision is the
        // precedent — a Batch-mode connection whose read-only view is declared RealTime and read
        // live. If this test ever fails, the one thing to check first is whether a live read on a
        // Batch connection has become forbidden somewhere, because that would change PerfectVision
        // too.
        var up = new AmplitudeSettings { AmplitudeVersion = AmplitudeVersion.Up };

        // The claim, stated on the read question alone: whatever the mode, an Up installation
        // answers a query. The write carrier is a different question and is asserted by the theory
        // above — conflating the two is what made this test fail when the write carrier changed,
        // for a reason that had nothing to do with reads.
        AmplitudeCarrierRouting.ServesApiReads(up).Should().BeTrue(
            $"the read path never consults the mode, and this connection is in {mode}");
    }

    [Fact]
    public void Unreadable_settings_route_to_the_batch_socle_rather_than_throwing()
    {
        // The caller may be the capability matrix of a tenant that has configured nothing at all.
        // "There is no API" is the right answer to that; an exception would make a screen that
        // merely asks what is supported fail.
        AmplitudeCarrierRouting.ChooseFor(null, IntegrationMode.Api)
            .Should().Be(AmplitudeCarrier.BatchSocle);

        AmplitudeCarrierRouting.ServesApiReads(null).Should().BeFalse();
    }

    [Fact]
    public void An_unknown_mode_narrows_the_carrier_and_never_widens_it()
    {
        // No connection means no mode. Narrowing is the only safe direction on an unknown: Batch
        // is the weaker promise — a command is deposited and closed later — while ApiServices would
        // declare live writes for an installation we have not read.
        var up = new AmplitudeSettings { AmplitudeVersion = AmplitudeVersion.Up };

        AmplitudeCarrierRouting.ChooseFor(up, mode: null)
            .Should().Be(AmplitudeCarrier.BatchSocle);
    }

    [Fact]
    public void An_unreadable_release_is_not_reported_as_a_mode_conflict()
    {
        // Two different faults with two different fixes. "We could not read the settings" must not
        // point an administrator at a mode that may be perfectly correct — the health check tells
        // the two apart, and this is the property that lets it.
        AmplitudeCarrierRouting.IsModeCoherent(null, IntegrationMode.Api).Should().BeTrue();
        AmplitudeCarrierRouting.IncoherenceDetail(null, IntegrationMode.Api).Should().BeNull();
    }

    [Fact]
    public void The_default_settings_record_is_already_the_narrow_case()
    {
        // AmplitudeSettings.AmplitudeVersion defaults to Legacy, which is what makes a row written
        // before the field meant anything safe without a migration. A default of Up would have
        // declared live writes for every existing connection.
        new AmplitudeSettings().AmplitudeVersion.Should().Be(AmplitudeVersion.Legacy);

        AmplitudeCarrierRouting.ChooseFor(new AmplitudeSettings(), IntegrationMode.Batch)
            .Should().Be(AmplitudeCarrier.BatchSocle);
    }

    [Fact]
    public void The_incoherence_message_names_both_fields_and_the_fix_in_both_directions()
    {
        var detail = AmplitudeCarrierRouting.IncoherenceDetail(
            new AmplitudeSettings { AmplitudeVersion = AmplitudeVersion.Legacy },
            IntegrationMode.Api);

        // Only the administrator knows which of the two fields is the wrong one: an institution
        // that really runs a pre-Up release needs the mode changed, one that has upgraded needs the
        // release changed. A message that assumed either would be wrong half the time, so both
        // values and both fixes are named.
        detail.Should().NotBeNull();
        detail.Should().Contain(nameof(AmplitudeVersion.Legacy));
        detail.Should().Contain(nameof(IntegrationMode.Api));
        detail.Should().Contain(nameof(IntegrationMode.Batch));
        detail.Should().Contain(nameof(AmplitudeVersion.Up));

        // And it is a configuration statement, not a procurement one: nothing here may suggest
        // waiting for SBS, because nobody needs to.
        detail.Should().NotContain(AmplitudeSpecification.Supplier);
    }
}
