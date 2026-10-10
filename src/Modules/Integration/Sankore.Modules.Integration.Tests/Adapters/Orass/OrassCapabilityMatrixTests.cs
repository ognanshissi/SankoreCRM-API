namespace Sankore.Modules.Integration.Tests.Adapters.Orass;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Orass;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// ASS-06, criterion 3 in the matrix — what an ORASS installation will serve, and in which mode.
/// <b>Deliverable in full</b>, because the matrix describes the shape of the integration and not a
/// wire format.
/// </summary>
public sealed class OrassCapabilityMatrixTests
{
    [Fact]
    public void An_open_api_on_an_api_connection_serves_the_writes_live_and_the_reads()
    {
        var matrix = OrassCapabilityMatrix.For(OrassHarness.ApiSettings(), IntegrationMode.Api);

        foreach (var write in OrassCapabilityMatrix.Writes)
            matrix.ModeOf(write).Should().Be(CapabilityMode.RealTime);

        foreach (var read in OrassCapabilityMatrix.ApiReads)
            matrix.ModeOf(read).Should().Be(CapabilityMode.RealTime);
    }

    [Fact]
    public void A_bordereau_installation_serves_the_same_three_writes_in_batch_and_no_read()
    {
        var matrix = OrassCapabilityMatrix.For(OrassHarness.BatchSettings(), IntegrationMode.Batch);

        // The list does not change with the carrier — that IS criterion 2's claim: an insurer fed
        // by file accepts a subscription, a cancellation and a claim all the same. Only the mode
        // differs, and Batch is a statement about the carrier rather than about speed.
        foreach (var write in OrassCapabilityMatrix.Writes)
            matrix.ModeOf(write).Should().Be(CapabilityMode.Batch);

        // And no read at all. In the insurance family that is not a stale figure but an absent one:
        // this module owns no policy table, so the gateway simply skips this connection and the 360
        // screen shows the customer with no cover. Declaring a read we cannot serve would be worse.
        foreach (var read in OrassCapabilityMatrix.ApiReads)
            matrix.Supports(read).Should().BeFalse();
    }

    [Fact]
    public void An_open_api_on_a_batch_connection_keeps_the_live_reads_and_batches_the_writes()
    {
        var matrix = OrassCapabilityMatrix.For(OrassHarness.ApiSettings(), IntegrationMode.Batch);

        // Writes follow the mode because the dispatcher does; reads follow the coordinates because
        // the read path never consults the mode. The two halves of OrassCarrierRouting, visible in
        // one matrix.
        matrix.ModeOf(IntegrationCapability.SubscribePolicy).Should().Be(CapabilityMode.Batch);
        matrix.ModeOf(IntegrationCapability.ReadPolicies).Should().Be(CapabilityMode.RealTime);
    }

    [Fact]
    public void An_unattributed_connection_declares_nothing_at_all()
    {
        var matrix = OrassCapabilityMatrix.For(
            OrassHarness.UnattributedSettings(), IntegrationMode.Api);

        // Criterion 3 showing up in the matrix and not only on the call path. Declaring buttons
        // whose every press answers a configuration error is bad everywhere; here it is worse,
        // because the reason the submission is refused is that the policy would otherwise end up in
        // nobody's portfolio.
        matrix.Modes.Should().BeEmpty();
    }

    [Fact]
    public void Unreadable_settings_narrow_to_nothing_rather_than_widening()
    {
        OrassCapabilityMatrix.For(null, IntegrationMode.Api).Modes.Should().BeEmpty();
        OrassCapabilityMatrix.For(null, null).Modes.Should().BeEmpty();
    }

    [Fact]
    public void An_incoherent_row_still_declares_the_writes_rather_than_hiding_the_product()
    {
        // A mode mistake is one field edit away, and ORASS really does accept these three
        // operations. Hiding every button would misdescribe the insurer for a reason that has
        // nothing to do with what it can do; the mistake is reported where it can be acted on — the
        // health answer — and not as a silently narrowed matrix.
        //
        // The row is mode Api with no base URL, which is now the only incoherent shape. It used to
        // be ApiSettings + Relay; that row still produces this exact matrix, but it is no longer
        // incoherent, so asserting it here would have left this test passing under a name that had
        // become false.
        var matrix = OrassCapabilityMatrix.For(OrassHarness.BatchSettings(), IntegrationMode.Api);

        matrix.Supports(IntegrationCapability.SubscribePolicy).Should().BeTrue();
        matrix.ModeOf(IntegrationCapability.SubscribePolicy).Should().Be(CapabilityMode.Batch);
    }

    [Fact]
    public void A_relay_row_gets_the_same_matrix_as_the_batch_row_it_is_equivalent_to()
    {
        var relayed = OrassCapabilityMatrix.For(OrassHarness.BatchSettings(), IntegrationMode.Relay);
        var direct = OrassCapabilityMatrix.For(OrassHarness.BatchSettings(), IntegrationMode.Batch);

        // The same insurer reached through the on-premise agent is the same insurer, and its writes
        // leave in a file either way (OutboundBatchCarrier). A tenant that moved behind a relay
        // agent must not lose buttons for a reason that has nothing to do with what its insurer can
        // do — which is the positive statement that replaces this matrix's old reading of Relay as
        // a fault.
        relayed.ToDictionary().Should().BeEquivalentTo(direct.ToDictionary());
    }

    [Fact]
    public void The_branch_changes_nothing_about_the_matrix()
    {
        var iard = OrassCapabilityMatrix.For(
            OrassHarness.ApiSettings(OrassBranch.Iard), IntegrationMode.Api);

        var vie = OrassCapabilityMatrix.For(
            OrassHarness.ApiSettings(OrassBranch.Vie), IntegrationMode.Api);

        // Deliberate, and the tempting alternative is wrong: every operation here exists on both
        // branches, and what differs between a life and a non-life undertaking is which PRODUCTS
        // answer and how a claim is handled — mapping concerns defined by the specification we do
        // not have. A matrix that narrowed on the branch would be encoding product knowledge nobody
        // here possesses.
        vie.ToDictionary().Should().BeEquivalentTo(iard.ToDictionary());
    }

    [Fact]
    public void The_core_banking_capabilities_are_never_declared_by_an_insurance_adapter()
    {
        var matrix = OrassCapabilityMatrix.For(OrassHarness.ApiSettings(), IntegrationMode.Api);

        foreach (var foreign in new[]
        {
            IntegrationCapability.CreateCustomer,
            IntegrationCapability.OpenAccount,
            IntegrationCapability.ReadBalance,
            IntegrationCapability.DebitAccount,
        })
        {
            matrix.Supports(foreign).Should().BeFalse();
        }
    }

    [Fact]
    public void Pricing_and_eligibility_are_not_declared_on_either_carrier()
    {
        // Out of ASS-06's scope, and absent for two different reasons depending on the carrier — on
        // a bordereau carrier a one-way file deposit can never quote anything, while on the API
        // carrier nothing tells us such an operation exists. The matrix cannot express that
        // difference, which is why OrassAdapter's remarks carry it; what the matrix CAN do is not
        // promise either.
        foreach (var mode in new[] { IntegrationMode.Api, IntegrationMode.Batch })
        {
            var matrix = OrassCapabilityMatrix.For(OrassHarness.ApiSettings(), mode);

            matrix.Supports(IntegrationCapability.PriceProduct).Should().BeFalse();
            matrix.Supports(IntegrationCapability.CheckEligibility).Should().BeFalse();
        }
    }

    [Fact]
    public void Every_declared_write_is_one_the_dispatcher_has_a_route_for()
    {
        // Declaring a fourth write would declare an operation nothing can queue:
        // ExecuteIntegrationCommandHandler dispatches on CommandType, and these three are the only
        // insurance ones it routes.
        OrassCapabilityMatrix.Writes.Should().BeEquivalentTo(new[]
        {
            IntegrationCapability.SubscribePolicy,
            IntegrationCapability.CancelPolicy,
            IntegrationCapability.DeclareClaim,
        });

        foreach (var write in OrassCapabilityMatrix.Writes)
            Enum.IsDefined(typeof(CommandType), write.ToString()).Should().BeTrue();
    }

    // ── A tenant's several ORASS connections, each on its own terms ────────────────────────
    //
    // This region used to hold six facts about OrassCapabilityMatrix.Narrowest, the INTERSECTION
    // of a tenant's ORASS matrices. It existed for one reason: ICbsAdapter's capability property
    // carried no connection, so an adapter asked "what can ORASS do" could not know which of the
    // tenant's rows the question was about, and the only safe whole-tenant answer was the weakest
    // one. CapabilitiesFor carries its subject, Narrowest is deleted, and the facts about an
    // intersection are facts about nothing.
    //
    // What survives of their purpose is the observation that made them necessary — two ORASS
    // installations of one tenant can differ — and it is now asserted as the matrix answering each
    // of them separately. Here at the matrix level, where the function is pure, and in
    // OrassAdapterTests.Two_connections_of_one_kind_each_get_their_own_matrix through the adapter.

    [Fact]
    public void Two_installations_of_one_tenant_keep_their_own_matrices()
    {
        var withApi = OrassCapabilityMatrix.For(OrassHarness.ApiSettings(), IntegrationMode.Api);
        var fileFed = OrassCapabilityMatrix.For(OrassHarness.BatchSettings(), IntegrationMode.Batch);

        // The case the intersection used to flatten, and the one ASS-01 makes ordinary: both
        // branches subscribe, and only the API-fed one answers a query. Under the narrowing the
        // API-fed branch lost its live policy list because its sibling is fed by bordereaux —
        // which is a statement about the OTHER insurer, made about this one.
        withApi.Supports(IntegrationCapability.SubscribePolicy).Should().BeTrue();
        withApi.Supports(IntegrationCapability.ReadPolicies).Should().BeTrue();
        withApi.ModeOf(IntegrationCapability.SubscribePolicy).Should().Be(CapabilityMode.RealTime);

        fileFed.Supports(IntegrationCapability.SubscribePolicy).Should().BeTrue();
        fileFed.Supports(IntegrationCapability.ReadPolicies).Should().BeFalse();
        fileFed.ModeOf(IntegrationCapability.SubscribePolicy).Should().Be(CapabilityMode.Batch);
    }

    [Fact]
    public void An_unattributed_installation_empties_its_own_matrix_and_no_other()
    {
        var sound = OrassCapabilityMatrix.For(OrassHarness.ApiSettings(), IntegrationMode.Api);

        var unattributed = OrassCapabilityMatrix.For(
            OrassHarness.UnattributedSettings(), IntegrationMode.Api);

        // Fail-closed stays fail-closed per row — a submission that cannot be attributed to this
        // institution would end up in nobody's portfolio, so that row declares nothing. What is no
        // longer true is "across the tenant": the sound row keeps its matrix, because the question
        // now names which connection it is about.
        unattributed.Modes.Should().BeEmpty();
        sound.Modes.Should().NotBeEmpty();
    }
}
