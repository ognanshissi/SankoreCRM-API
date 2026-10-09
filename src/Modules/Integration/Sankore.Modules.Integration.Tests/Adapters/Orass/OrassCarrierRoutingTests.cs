namespace Sankore.Modules.Integration.Tests.Adapters.Orass;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Orass;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// ASS-06, criterion 2 — « repli en mode batch (bordereaux par fichier) sur le socle commun si
/// l'API n'est pas ouverte chez l'assureur ». <b>Deliverable in full, and deliverable without
/// anybody's specification</b>, because the decision is ours: it reads one configured coordinate
/// and the connection's own mode.
///
/// <para>
/// Six combinations, two coordinate states by three modes, and every one of them asserted — the
/// table in <c>OrassCarrierRouting</c>'s remarks is the specification these tests pin.
/// </para>
///
/// <para>
/// The pair worth reading first concerns <see cref="IntegrationMode.Relay"/>, because the two
/// questions it raises have DIFFERENT answers and an earlier version of this file ran them
/// together. Is a relay write a call? No — it leaves in a file
/// (<c>OutboundBatchCarrier.LeavesInAFile</c>), which is what
/// <see cref="OrassCarrierRouting.ChooseFor"/> encodes. Is that a misconfiguration? It was, while
/// the dispatcher enlisted such a write and the scheduled generation pass could not see it; that
/// split is closed, so it is not. The two tests named for that mode assert one half each, and the
/// positive one exists so the old verdict cannot come back unnoticed.
/// </para>
/// </summary>
public sealed class OrassCarrierRoutingTests
{
    // ── The six combinations ────────────────────────────────────────────────────────────────

    [Fact]
    public void An_open_api_on_an_api_connection_travels_on_the_api()
    {
        // The intended shape of criterion 1: the insurer opened something, and the connection says
        // we call it.
        OrassCarrierRouting
            .ChooseFor(OrassHarness.ApiSettings(), IntegrationMode.Api)
            .Should().Be(OrassCarrier.ExternalApi);
    }

    [Fact]
    public void An_open_api_on_a_batch_connection_still_travels_in_bordereaux()
    {
        // Legitimate, and a choice rather than a mistake: an insurer can expose an API and still be
        // fed by file. The decisive reason it must be Batch is not preference, it is the dispatcher:
        // ExecuteIntegrationCommandHandler diverts a Batch connection's writes to the socle BEFORE
        // resolving an adapter, so declaring this RealTime would describe a path that does not run.
        OrassCarrierRouting
            .ChooseFor(OrassHarness.ApiSettings(), IntegrationMode.Batch)
            .Should().Be(OrassCarrier.BatchSocle);
    }

    [Fact]
    public void A_closed_api_on_a_batch_connection_is_the_fallback_the_criterion_asks_for()
    {
        OrassCarrierRouting
            .ChooseFor(OrassHarness.BatchSettings(), IntegrationMode.Batch)
            .Should().Be(OrassCarrier.BatchSocle);
    }

    [Fact]
    public void A_closed_api_on_an_api_connection_falls_back_and_is_reported_incoherent()
    {
        var settings = OrassHarness.BatchSettings();

        // Narrow, because there is nothing to call. And flagged, because the narrowing describes no
        // working path either: the dispatcher would hand this row's writes to an adapter.
        OrassCarrierRouting.ChooseFor(settings, IntegrationMode.Api)
            .Should().Be(OrassCarrier.BatchSocle);

        OrassCarrierRouting.IsModeCoherent(settings, IntegrationMode.Api).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_relay_connection_is_never_a_call_carrier(bool apiIsOpen)
    {
        var settings = apiIsOpen ? OrassHarness.ApiSettings() : OrassHarness.BatchSettings();

        // The surviving half of what this test used to claim, and it is still the load-bearing
        // half. OrassSettings derives from BatchCapableSettings, and OutboundBatchCarrier —
        // the module's single definition of "leaves in a file" — answers true for mode Relay with
        // file-capable settings. So every relay ORASS write is deposited, never dialled, and
        // calling this ExternalApi would contradict the dispatcher outright.
        //
        // Pinned in BOTH coordinate states on purpose: an open API does not make a relay write a
        // call. That is also why ChooseFor's predicate is spelled `mode is Api` and not
        // `mode is Api or Relay` — the second reads as "the transport differs, not whether an
        // answer comes back", which is true of a relay HTTP call and false of a relay file deposit.
        //
        // This test no longer asserts that the shape is INCOHERENT; it once did, and that half was
        // wrong. See A_relay_connection_with_file_coordinates_is_accepted for the replacement, and
        // OrassCarrierRouting's remarks for why the verdict changed.
        OrassCarrierRouting.ChooseFor(settings, IntegrationMode.Relay)
            .Should().Be(OrassCarrier.BatchSocle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_relay_connection_with_file_coordinates_is_accepted(bool apiIsOpen)
    {
        var settings = apiIsOpen ? OrassHarness.ApiSettings() : OrassHarness.BatchSettings();

        // The positive case, and it exists so that a later change cannot silently make this mode a
        // fault again — narrowing the theory above alone would have left nothing asserting that
        // Relay is now accepted.
        //
        // Why it is accepted: this class used to report every relay ORASS connection as incoherent,
        // and that was the right answer to a platform that could not carry the write — the
        // dispatcher enlisted it into the socle while the scheduled generation pass scanned
        // Mode == Batch alone, so the bordereau was never produced and every subscription waited
        // out its AckTimeoutHours. All three paths now read OutboundBatchCarrier, the scheduled
        // pass generates and deposits a relay connection's file like any other, and refusing a
        // shape that works would send an administrator to correct a field that is already right.
        //
        // Both coordinate states again: whether the insurer opened an API is irrelevant to whether
        // a file can be deposited through the agent.
        OrassCarrierRouting.IsModeCoherent(settings, IntegrationMode.Relay).Should().BeTrue();
        OrassCarrierRouting.IncoherenceDetail(settings, IntegrationMode.Relay).Should().BeNull();
    }

    // ── Unknowns narrow, and the shape of the predicate is the reason ───────────────────────

    [Fact]
    public void An_unknown_mode_narrows_to_the_batch_socle_rather_than_widening()
    {
        // The trap this spelling exists to avoid: `mode is not IntegrationMode.Batch` is TRUE for
        // null AND for Relay, so the negative form would hand both the widest answer. The mistake
        // compiles and reads correctly, which is why it is pinned rather than trusted to review.
        OrassCarrierRouting
            .ChooseFor(OrassHarness.ApiSettings(), mode: null)
            .Should().Be(OrassCarrier.BatchSocle);
    }

    [Fact]
    public void Unreadable_settings_narrow_to_the_batch_socle_and_serve_no_live_read()
    {
        OrassCarrierRouting.ChooseFor(null, IntegrationMode.Api).Should().Be(OrassCarrier.BatchSocle);
        OrassCarrierRouting.ServesApiReads(null).Should().BeFalse();
        OrassCarrierRouting.HasApiCoordinates(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_base_url_means_the_insurer_opened_nothing(string? baseUrl)
    {
        var settings = new OrassSettings
        {
            BaseUrl = baseUrl,
            IntermediaryCode = OrassHarness.PlaceholderIntermediaryCode,
        };

        // Whitespace counts as absent deliberately: a cleared field on INT-03's settings form posts
        // " ", and treating that as an address would declare a live subscription capability
        // pointing at nothing.
        OrassCarrierRouting.HasApiCoordinates(settings).Should().BeFalse();
        OrassCarrierRouting.ChooseFor(settings, IntegrationMode.Api).Should().Be(OrassCarrier.BatchSocle);
    }

    // ── Reads follow the coordinates, writes follow the mode ────────────────────────────────

    [Fact]
    public void A_read_follows_the_coordinates_and_not_the_mode()
    {
        var settings = OrassHarness.ApiSettings();

        // The asymmetry is the finding, not an inconsistency: a read is not a command, there is no
        // CommandType for one, and the dispatcher is the only thing that routes on the mode. So an
        // insurer with an open API answers reads live even where its subscriptions travel in
        // bordereaux — which is exactly the shape Perfect Vision already has on the CBS side.
        OrassCarrierRouting.ChooseFor(settings, IntegrationMode.Batch)
            .Should().Be(OrassCarrier.BatchSocle);

        OrassCarrierRouting.ServesApiReads(settings).Should().BeTrue();
    }

    [Fact]
    public void A_closed_api_serves_no_live_read_at_all()
    {
        OrassCarrierRouting.ServesApiReads(OrassHarness.BatchSettings()).Should().BeFalse();
    }

    // ── Coherence: what is reported, and what is deliberately not ──────────────────────────

    [Fact]
    public void Nothing_configured_is_not_reported_as_incoherent()
    {
        // "We could not read the coordinates" and "the coordinates contradict the mode" are
        // different faults with different fixes. Collapsing them would point an administrator at a
        // mode that may be perfectly correct.
        OrassCarrierRouting.IsModeCoherent(null, IntegrationMode.Api).Should().BeTrue();
        OrassCarrierRouting.IsModeCoherent(OrassHarness.ApiSettings(), null).Should().BeTrue();

        OrassCarrierRouting.IncoherenceDetail(null, IntegrationMode.Api).Should().BeNull();
        OrassCarrierRouting.IncoherenceDetail(OrassHarness.ApiSettings(), null).Should().BeNull();
    }

    [Fact]
    public void The_two_canonical_shapes_report_nothing()
    {
        // "Canonical" and not "the two coherent ones": a relay connection is coherent too, and so
        // is an open API fed by file. These are the two the criterion itself names — the API path
        // and its bordereau fallback.
        OrassCarrierRouting
            .IncoherenceDetail(OrassHarness.ApiSettings(), IntegrationMode.Api)
            .Should().BeNull();

        OrassCarrierRouting
            .IncoherenceDetail(OrassHarness.BatchSettings(), IntegrationMode.Batch)
            .Should().BeNull();
    }

    [Fact]
    public void The_api_incoherence_names_the_fix_in_both_directions()
    {
        var detail = OrassCarrierRouting.IncoherenceDetail(
            OrassHarness.BatchSettings(), IntegrationMode.Api);

        // Both, because only the administrator knows which field is the wrong one: an insurer that
        // really opened nothing needs the mode changed, one that opened an API needs the base URL
        // filled in. A message that assumed either would be wrong half the time.
        detail.Should().NotBeNull();
        detail.Should().Contain(nameof(IntegrationMode.Batch));
        detail.Should().Contain("settings.baseUrl");
    }

    [Fact]
    public void There_is_exactly_one_incoherent_shape_and_a_relay_connection_is_not_it()
    {
        // This test used to assert that the Relay incoherence message named the shape that WOULD
        // work, rather than merely refusing. There is no such message any more, so its purpose
        // moves one level up and stays the same in substance: guard the contents of the incoherent
        // set, so that nothing is refused for which an administrator has nothing to correct.
        //
        // The set has exactly one member. Enumerated exhaustively over both coordinate states and
        // all three modes rather than spot-checked, because the failure this protects against is an
        // ADDITION to the set — and a spot check cannot see one.
        var incoherent = new List<string>();

        foreach (var (label, settings) in new (string, Domain.OrassSettings)[]
        {
            ("api-open", OrassHarness.ApiSettings()),
            ("api-closed", OrassHarness.BatchSettings()),
        })
        foreach (var mode in Enum.GetValues<IntegrationMode>())
        {
            if (!OrassCarrierRouting.IsModeCoherent(settings, mode))
                incoherent.Add($"{label}+{mode}");
        }

        incoherent.Should().Equal(["api-closed+Api"]);

        // And no stale message survives for the shape that is now accepted: a leftover Relay branch
        // in IncoherenceDetail would be unreachable text that the next reader takes for the rule.
        OrassCarrierRouting
            .IncoherenceDetail(OrassHarness.BatchSettings(), IntegrationMode.Api)
            .Should().NotContain(nameof(IntegrationMode.Relay));
    }

    [Fact]
    public void The_incoherence_message_never_sends_the_operator_to_the_supplier()
    {
        // The whole point of separating a configuration fault from the pending specification: a
        // mistake the administrator can fix in one edit must never read as "waiting for ORSYS".
        //
        // One detail and no loop, now that the incoherent set has one member. It used to iterate
        // over two, and the second became null when Relay stopped being a fault — a loop over a
        // null-forgiven null is a test that keeps passing while asserting nothing, which is worse
        // than one that fails.
        var detail = OrassCarrierRouting.IncoherenceDetail(
            OrassHarness.BatchSettings(), IntegrationMode.Api);

        detail.Should().NotBeNull();
        detail.Should().NotContain(OrassSpecification.Supplier);
        detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);
    }
}
