namespace Sankore.Modules.Integration.Tests.Adapters.Orass;

using System.Reflection;
using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Orass;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// ASS-06, criterion 1 — the insurance ports are in place and every one of them refuses, naming
/// what has not arrived.
///
/// <para>
/// What these tests defend is not a behaviour, it is a decision: that an adapter whose
/// specification is missing must say so rather than guess. M02's biometry client is the
/// counter-example the repository already paid for — hand-written wire records against a document
/// nobody had read, every field mapping to null, every KYC file stuck in <c>Verifying</c> against a
/// service that answered perfectly. A guessed bordereau fails more quietly still: the file is
/// deposited, the transfer succeeds, and the command waits forever for an acknowledgement the
/// insurer will never send because it could not read what we sent. And in THIS family the quiet
/// failure has a name — a customer who believes they are insured and is not, discovered at the
/// claim.
/// </para>
/// </summary>
public sealed class OrassAdapterTests
{
    /// <summary>
    /// Every method of every port this adapter implements, discovered rather than listed.
    ///
    /// <para>
    /// Reflection on purpose. A hand-written list of nine calls is a list that goes stale the day a
    /// port grows a tenth method — exactly the day it matters, because a new port method returning
    /// a default <c>IntegrationResult</c> would be the only silent success in an adapter whose
    /// whole contract is to refuse.
    /// </para>
    /// </summary>
    public static TheoryData<string> PortMethods()
    {
        var data = new TheoryData<string>();

        foreach (var name in PortMethodNames()) data.Add(name);

        return data;
    }

    [Theory]
    [MemberData(nameof(PortMethods))]
    public async Task Every_port_method_refuses_and_names_the_missing_specification(string methodName)
    {
        using var harness = OrassHarness.With(OrassHarness.ApiSettings());

        var result = await InvokeAsync(harness.Adapter, methodName);

        result.IsFailure.Should().BeTrue($"{methodName} cannot be served without the specification");
        result.Code.Should().Be(IntegrationErrors.AdapterSpecificationPending);

        // Technical and never Transient: the family is what decides what the platform does next,
        // and retrying with backoff cannot obtain a vendor document or a partner's agreement.
        // Technical parks the command and alerts the administrator, which is the action that can
        // actually move this forward.
        result.Family.Should().Be(ErrorFamily.Technical);
        result.IsRetryable.Should().BeFalse();

        // The operator-facing half. An error that said "not implemented" would send someone to
        // raise a defect and someone else to look for the half-written method; this one says what
        // is missing, who owns it, and where that is recorded.
        result.Detail.Should().NotBeNullOrWhiteSpace();
        result.Detail.Should().Contain(OrassSpecification.PlanReference);
        result.Detail.Should().Contain(OrassSpecification.Counterparty);
        result.Detail.Should().NotContain("not implemented");
    }

    [Fact]
    public async Task The_refusal_names_the_artefact_this_installations_carrier_needs()
    {
        using var onTheApi = OrassHarness.With(OrassHarness.ApiSettings());

        using var onBordereaux = OrassHarness.With(
            OrassHarness.BatchSettings(), mode: IntegrationMode.Batch);

        var api = await onTheApi.Adapter.SubscribeAsync(Payload(), Key(), default);
        var batch = await onBordereaux.Adapter.SubscribeAsync(Payload(), Key(), default);

        // Two separate asks, of two different teams about two different carriers, and an answer to
        // one unblocks nothing about the other. An operator who forwards "we need the ORASS
        // specification" gets a conversation; one who forwards the right half gets a document.
        api.Detail.Should().Contain("operation catalogue");
        batch.Detail.Should().Contain("bordereau layout");

        api.Detail.Should().NotContain("bordereau layout");
        batch.Detail.Should().NotContain("operation catalogue");
    }

    [Fact]
    public async Task The_refusal_names_the_branch_so_the_operator_knows_which_company_to_ask()
    {
        using var vie = OrassHarness.With(OrassHarness.ApiSettings(OrassBranch.Vie));

        var result = await vie.Adapter.SubscribeAsync(Payload(), Key(), default);

        // In the CIMA zone IARD and Vie are separate undertakings, so this is not a label: it is
        // which legal entity the conversation is with.
        result.Detail.Should().Contain(nameof(OrassBranch.Vie));
        result.Detail.Should().NotContain(nameof(OrassBranch.Iard));
    }

    [Fact]
    public async Task A_tenant_whose_two_orass_rows_disagree_gets_both_branches_named()
    {
        using var harness = OrassHarness.WithBoth(
            (OrassHarness.ApiSettings(OrassBranch.Iard), IntegrationMode.Api),
            (OrassHarness.ApiSettings(OrassBranch.Vie), IntegrationMode.Api));

        var result = await harness.Adapter.SubscribeAsync(Payload(), Key(), default);

        // Null branch names both rather than picking one. Guessing would send somebody to the life
        // company about a non-life submission — and the ports carry no connection, so the adapter
        // genuinely cannot tell which row the call is for.
        result.Detail.Should().Contain(nameof(OrassBranch.Iard));
        result.Detail.Should().Contain(nameof(OrassBranch.Vie));
    }

    [Fact]
    public void The_adapter_answers_for_its_own_kind()
    {
        using var harness = OrassHarness.With(OrassHarness.ApiSettings());

        // IntegrationAdapterResolver keys on connection.Kind.ToString() and nothing else.
        harness.Adapter.Kind.Should().Be(IntegrationKind.Orass);
    }

    // ── The refusal chain, in the order of a real submission ───────────────────────────────

    [Fact]
    public async Task An_unattributed_connection_is_refused_before_anything_else()
    {
        // No intermediary code AND no credential stored: two faults at once, and the attribution
        // one must win. Scope first, always — so that the day refusals are removed one method at a
        // time the attribution check is already the only path to a result, rather than a line
        // somebody has to remember to add.
        using var harness = OrassHarness.With(
            OrassHarness.UnattributedSettings(), credentialsStored: false);

        var result = await harness.Adapter.SubscribeAsync(Payload(), Key(), default);

        result.Code.Should().Be(IntegrationErrors.SettingsInvalid);
        result.Detail.Should().Contain("IntermediaryCode");
    }

    [Fact]
    public async Task An_incoherent_mode_is_refused_before_the_credential()
    {
        // Two faults at once on purpose — that is the only way to assert an ORDER — and the
        // coherence one must win. The carrier is what decides WHICH credential is wanted, so a row
        // whose coordinates and mode contradict each other has no carrier, and a credential verdict
        // on it would have to name an arbitrary one of the two sets.
        //
        // The incoherent shape used here is mode Api with no base URL. It used to be mode Relay,
        // which is no longer a fault: a relay write leaves in a file and the scheduled pass now
        // generates and deposits it. Swapped rather than deleted, because the ordering this test
        // protects is unchanged and still only observable with a genuinely incoherent row.
        using var harness = OrassHarness.With(
            OrassHarness.BatchSettings(), credentialsStored: false, mode: IntegrationMode.Api);

        var result = await harness.Adapter.SubscribeAsync(Payload(), Key(), default);

        result.Code.Should().Be(IntegrationErrors.SettingsInvalid);
        result.Code.Should().NotBe(IntegrationErrors.CredentialMissing);
        result.Detail.Should().Contain("settings.baseUrl");
    }

    [Fact]
    public async Task A_relay_connection_with_file_coordinates_is_not_reported_as_a_fault()
    {
        // The other half of the swap above, and the reason it is a swap rather than a deletion:
        // narrowing the incoherent set alone would have left nothing asserting that a relay
        // connection is now ACCEPTED, so a later change could silently make it a fault again.
        using var harness = OrassHarness.With(
            OrassHarness.ApiSettings(), mode: IntegrationMode.Relay);

        var connection = OrassHarness.ConnectionCarrying(
            OrassHarness.ApiSettings(), IntegrationMode.Relay);

        var health = await harness.Adapter.CheckHealthAsync(connection, default);

        // Still Unhealthy — the structural lock holds, and must: no ORASS connection may be
        // activated until the specification arrives and the insurer agrees. But the REASON has to
        // be the missing specification and NOT a configuration fault, because there is nothing here
        // for an administrator to correct: the writes leave in a file, and the scheduled pass
        // produces and deposits that file (OutboundBatchCarrier). Reporting SETTINGS_INVALID would
        // send somebody to change a mode that is already right.
        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.AdapterSpecificationPending);
        health.Detail.Should().NotContain(IntegrationErrors.SettingsInvalid);

        // And a subscription refuses for the same reason, so the call path and the activation
        // screen tell one story.
        var result = await harness.Adapter.SubscribeAsync(Payload(), Key(), default);

        result.Code.Should().Be(IntegrationErrors.AdapterSpecificationPending);
    }

    [Fact]
    public async Task A_missing_credential_is_its_own_fault_and_not_the_specification()
    {
        using var harness = OrassHarness.With(OrassHarness.ApiSettings(), credentialsStored: false);

        var result = await harness.Adapter.SubscribeAsync(Payload(), Key(), default);

        // Different owner, different screen: one vault write by the tenant's own administrator
        // versus a conversation with ORSYS and the insurer. Collapsing them sends the wrong person
        // to the wrong place — and, worse, has them wait for a document that would fix nothing.
        result.Code.Should().Be(IntegrationErrors.CredentialMissing);
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Detail.Should().Contain("INT-03");
    }

    [Fact]
    public async Task A_bordereau_connection_is_refused_for_its_OWN_carriers_credentials()
    {
        using var harness = OrassHarness.With(
            OrassHarness.BatchSettings(), credentialsStored: false, mode: IntegrationMode.Batch);

        var result = await harness.Adapter.SubscribeAsync(Payload(), Key(), default);

        // The ORASS-only fork: an API-fed insurer needs one vault entry, a file-fed one needs the
        // SFTP credential and the host-key fingerprint. Asking for the API credential here would
        // send an administrator to save something this connection will never use.
        result.Code.Should().Be(IntegrationErrors.CredentialMissing);
        result.Detail.Should().Contain("SFTP");
    }

    [Fact]
    public async Task One_misconfigured_row_refuses_the_whole_tenant()
    {
        using var harness = OrassHarness.WithBoth(
            (OrassHarness.ApiSettings(OrassBranch.Iard), IntegrationMode.Api),
            (OrassHarness.UnattributedSettings(), IntegrationMode.Api));

        var result = await harness.Adapter.SubscribeAsync(Payload(), Key(), default);

        // Fail-closed, and this is the test that says why it has to be: the port methods carry no
        // connection — a subscription arrives as a payload and an idempotency key so that a
        // consumer module never has to know a connection exists — so the adapter cannot tell which
        // of the tenant's ORASS rows a call is for. Assuming the sound one costs a policy in
        // nobody's portfolio; refusing costs a form field and a retry.
        result.Code.Should().Be(IntegrationErrors.SettingsInvalid);
    }

    [Fact]
    public async Task A_tenant_with_no_orass_connection_is_told_that_and_not_about_a_document()
    {
        using var harness = OrassHarness.WithNoConnection();

        var result = await harness.Adapter.SubscribeAsync(Payload(), Key(), default);

        result.Code.Should().Be(IntegrationErrors.NoActiveConnection);
        result.Detail.Should().NotContain(OrassSpecification.MissingDocument);
    }

    // ── The matrix, read from the connection it is handed ──────────────────────────────────

    [Fact]
    public void The_matrix_is_read_from_the_connection_it_is_handed()
    {
        // Was two harnesses compared through a parameterless Capabilities property, which could
        // only ever express "this KIND's matrix". Same purpose — criterion 2's carrier decides the
        // matrix — asserted the way the contract now states it: ONE adapter, two rows, two answers.
        using var harness = OrassHarness.With(OrassHarness.ApiSettings());

        var onTheApi = OrassHarness.ConnectionCarrying(OrassHarness.ApiSettings());

        var onBordereaux = OrassHarness.ConnectionCarrying(
            OrassHarness.BatchSettings(), IntegrationMode.Batch, OrassHarness.SecondConnectionId);

        harness.Adapter.CapabilitiesFor(onTheApi).ModeOf(IntegrationCapability.SubscribePolicy)
            .Should().Be(CapabilityMode.RealTime);

        harness.Adapter.CapabilitiesFor(onBordereaux).ModeOf(IntegrationCapability.SubscribePolicy)
            .Should().Be(CapabilityMode.Batch, "criterion 2's fallback, declared as such");

        harness.Adapter.CapabilitiesFor(onTheApi).Supports(IntegrationCapability.ReadPolicies)
            .Should().BeTrue();

        harness.Adapter.CapabilitiesFor(onBordereaux).Supports(IntegrationCapability.ReadPolicies)
            .Should().BeFalse("a one-way bordereau answers no query, and there is no policy snapshot");
    }

    [Fact]
    public void A_connection_that_is_not_active_still_yields_a_matrix()
    {
        // Deliberate, and the opposite of TemenosAdapter's binding. An ORASS connection can never
        // be activated — its health check cannot pass until the specification arrives — so
        // refusing an inactive row would make the matrix permanently empty and criterion 3
        // unobservable. Answering for one grants nothing: every port refuses regardless.
        using var harness = OrassHarness.With(OrassHarness.ApiSettings());

        // ConnectionCarrying never activates the row it builds, which is the state under test.
        var inactive = OrassHarness.ConnectionCarrying(OrassHarness.ApiSettings());

        inactive.IsActive.Should().BeFalse("otherwise this test is about an active connection");

        harness.Adapter.CapabilitiesFor(inactive).Modes.Should().NotBeEmpty();
    }

    [Fact]
    public void A_row_carrying_another_kinds_settings_gets_an_empty_matrix_rather_than_an_exception()
    {
        // Was "a tenant with no connection": the matrix used to be looked up by the adapter, so
        // "no row" was the narrowest input it could be given. CapabilitiesFor takes a row, so the
        // narrowest input is now a row whose settings it cannot read as ORASS's — unattributable,
        // and therefore nothing. A screen asking what ORASS supports must get an answer rather
        // than a fault.
        using var harness = OrassHarness.With(OrassHarness.ApiSettings());

        var foreign = OrassHarness.ConnectionCarrying(
            new TemenosSettings { BaseUrl = "https://example.invalid" });

        harness.Adapter.CapabilitiesFor(foreign).Modes.Should().BeEmpty();
    }

    [Fact]
    public void Two_connections_of_one_kind_each_get_their_own_matrix()
    {
        // The replacement for Two_connections_narrow_the_matrix_to_what_both_serve, and the reason
        // the narrowing existed at all: the capability property carried no connection, so an
        // adapter asked "what can ORASS do" could only answer for the whole tenant — and the only
        // safe whole-tenant answer was the INTERSECTION of its rows. CapabilitiesFor carries the
        // subject, so the intersection is gone and each row answers for itself.
        //
        // Insurance is the family where that matters: ASS-01 places no single-active-connection
        // limit on it, and the CIMA separation of IARD and Vie undertakings makes two ACTIVE ORASS
        // rows the normal shape for an IMF selling both. Under the narrowing, the API-fed branch
        // lost its live policy list because the OTHER branch is fed by bordereaux.
        using var harness = OrassHarness.WithBoth(
            (OrassHarness.ApiSettings(OrassBranch.Iard), IntegrationMode.Api),
            (OrassHarness.BatchSettings(OrassBranch.Vie), IntegrationMode.Batch));

        var iard = OrassHarness.ConnectionCarrying(
            OrassHarness.ApiSettings(OrassBranch.Iard), IntegrationMode.Api);

        var vie = OrassHarness.ConnectionCarrying(
            OrassHarness.BatchSettings(OrassBranch.Vie),
            IntegrationMode.Batch,
            OrassHarness.SecondConnectionId);

        harness.Adapter.CapabilitiesFor(iard).ModeOf(IntegrationCapability.SubscribePolicy)
            .Should().Be(CapabilityMode.RealTime, "this insurer opened an API");

        harness.Adapter.CapabilitiesFor(iard).Supports(IntegrationCapability.ReadPolicies)
            .Should().BeTrue("and the live reads come with it");

        harness.Adapter.CapabilitiesFor(vie).ModeOf(IntegrationCapability.SubscribePolicy)
            .Should().Be(CapabilityMode.Batch, "this one is fed by bordereaux");

        harness.Adapter.CapabilitiesFor(vie).Supports(IntegrationCapability.ReadPolicies)
            .Should().BeFalse("a one-way bordereau answers no query");
    }

    // ── Health (INT-03), and the structural lock ───────────────────────────────────────────

    [Fact]
    public async Task The_health_check_answers_unhealthy_instead_of_throwing()
    {
        using var harness = OrassHarness.With(OrassHarness.ApiSettings());

        var connection = OrassHarness.ConnectionCarrying(OrassHarness.ApiSettings());

        var health = await harness.Adapter.CheckHealthAsync(connection, default);

        // Called from the activation screen and from the `integration` health check. A throwing
        // adapter would turn a known supplier dependency into a 500 that reads like an outage of
        // ours.
        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.AdapterSpecificationPending);
        health.Detail.Should().Contain(OrassSpecification.PlanReference);

        // No latency, because nothing was called. Reporting zero would put this row in the
        // activation screen's latency column next to systems that actually answered.
        health.Latency.Should().BeNull();
        health.CheckedAt.Should().NotBe(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task A_failed_health_check_is_what_keeps_the_connection_inactive()
    {
        using var harness = OrassHarness.With(OrassHarness.ApiSettings());

        var connection = OrassHarness.ConnectionCarrying(OrassHarness.ApiSettings());

        var health = await harness.Adapter.CheckHealthAsync(connection, default);
        connection.RecordHealth(health, TimeProvider.System);

        var activation = connection.Activate(Guid.NewGuid(), TimeProvider.System);

        // THE structural lock, and the ORASS stakes are the sharpest in the module. An active ORASS
        // connection in Batch mode would never reach this adapter at all:
        // ExecuteIntegrationCommandHandler routes a write to the batch socle on the MODE, before
        // resolving an adapter, and OrassSettings is file-based — so none of the refusals above
        // would ever be consulted. OutboundBatchFileGenerator would fall back to
        // DelimitedOutboundBatchFormatter (an honest self-describing projection, and emphatically
        // not an ORASS bordereau), the SFTP deposit would report success, and every subscription
        // would wait out its ackTimeoutHours while a counter clerk had already told the customer
        // their cover was in force. The health gate is the only thing standing in that path.
        activation.IsSuccess.Should().BeFalse();
        activation.Error.Should().Be(IntegrationErrors.ConnectionNotHealthy);
        connection.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Settings_of_the_wrong_shape_are_reported_as_their_own_configuration_fault()
    {
        using var harness = OrassHarness.With(OrassHarness.ApiSettings());

        // A row whose settings are not ORASS's. Defence in depth — the aggregate refuses the
        // mismatch on write — but the two faults send an administrator to different screens, and
        // collapsing them would send someone to the insurer over a mistake they can fix
        // themselves.
        var foreign = OrassHarness.ConnectionCarrying(
            new TemenosSettings { BaseUrl = "https://example.invalid" });

        var health = await harness.Adapter.CheckHealthAsync(foreign, default);

        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
        health.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);
    }

    [Fact]
    public async Task The_health_check_reports_each_configuration_fault_separately()
    {
        // THE test that protects the five-owner distinction, and it has to stay a SEPARATION test
        // rather than drift into "they all fail". Losing Relay from the incoherent set cost it one
        // case, so what remains is asserted the other way round as well: each verdict carries its
        // own marker AND carries none of the others. An activation screen that answered
        // "specification pending" to a missing intermediary code would have an administrator wait
        // months for a document that would not have fixed their connection — and the inverse, a
        // configuration code shown for a block only a partner can lift, teaches them to ignore the
        // screen.
        using var harness = OrassHarness.With(OrassHarness.ApiSettings(), credentialsStored: false);
        using var credentialled = OrassHarness.With(OrassHarness.ApiSettings());

        // 1. The settings do not belong to this kind.
        var foreignShape = await credentialled.Adapter.CheckHealthAsync(
            OrassHarness.ConnectionCarrying(
                new TemenosSettings { BaseUrl = "https://example.invalid" }),
            default);

        // 2. Nothing says who is submitting.
        var unattributed = await credentialled.Adapter.CheckHealthAsync(
            OrassHarness.ConnectionCarrying(OrassHarness.UnattributedSettings()), default);

        // 3. The coordinates and the mode contradict each other — mode Api with no API. This is
        //    now the ONLY incoherent shape; it used to be joined by mode Relay, which the platform
        //    has since learned to carry.
        var incoherent = await credentialled.Adapter.CheckHealthAsync(
            OrassHarness.ConnectionCarrying(OrassHarness.BatchSettings(), IntegrationMode.Api),
            default);

        // 4. The carrier is settled and its credential is not stored.
        var uncredentialled = await harness.Adapter.CheckHealthAsync(
            OrassHarness.ConnectionCarrying(OrassHarness.ApiSettings()), default);

        // 5. Everything this side owns is in place, and a partner deliverable is not.
        var pending = await credentialled.Adapter.CheckHealthAsync(
            OrassHarness.ConnectionCarrying(OrassHarness.ApiSettings()), default);

        foreach (var health in new[]
        {
            foreignShape, unattributed, incoherent, uncredentialled, pending,
        })
        {
            health.IsHealthy.Should().BeFalse("the structural lock holds in every branch");
        }

        // The separation itself: three distinct codes across five verdicts, each exclusive of the
        // other two. Asserting only the positive half is what would let two owners collapse into
        // one without a test noticing.
        foreignShape.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
        foreignShape.Detail.Should().NotContain(IntegrationErrors.CredentialMissing);
        foreignShape.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);

        unattributed.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
        unattributed.Detail.Should().Contain("IntermediaryCode");
        unattributed.Detail.Should().NotContain(IntegrationErrors.CredentialMissing);
        unattributed.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);

        incoherent.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
        incoherent.Detail.Should().Contain("settings.baseUrl");
        incoherent.Detail.Should().NotContain(IntegrationErrors.CredentialMissing);
        incoherent.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);

        uncredentialled.Detail.Should().Contain(IntegrationErrors.CredentialMissing);
        uncredentialled.Detail.Should().NotContain(IntegrationErrors.SettingsInvalid);
        uncredentialled.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);

        pending.Detail.Should().Contain(IntegrationErrors.AdapterSpecificationPending);
        pending.Detail.Should().NotContain(IntegrationErrors.SettingsInvalid);
        pending.Detail.Should().NotContain(IntegrationErrors.CredentialMissing);

        // And the three settings faults are not interchangeable either: same code, different
        // message, because they are three different screens even though one error code covers
        // them.
        new[] { foreignShape.Detail, unattributed.Detail, incoherent.Detail }
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_missing_credential_is_reported_by_the_health_check_as_its_own_fault()
    {
        using var harness = OrassHarness.With(OrassHarness.ApiSettings(), credentialsStored: false);

        var health = await harness.Adapter.CheckHealthAsync(
            OrassHarness.ConnectionCarrying(OrassHarness.ApiSettings()), default);

        health.Detail.Should().Contain(IntegrationErrors.CredentialMissing);
        health.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);
    }

    [Fact]
    public async Task The_health_check_reads_the_row_it_is_handed_and_not_the_bound_one()
    {
        // It runs BEFORE activation, on a connection the administrator may have just created, so
        // there may be nothing for Bind() to find — and for ORASS there is a second reason: this is
        // the ONE path in the module that knows which insurance connection the question is about,
        // which is why the structural lock belongs here rather than on a port.
        using var harness = OrassHarness.WithNoConnection();

        var health = await harness.Adapter.CheckHealthAsync(
            OrassHarness.ConnectionCarrying(OrassHarness.ApiSettings()), default);

        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.AdapterSpecificationPending);
        health.Detail.Should().NotContain(IntegrationErrors.NoActiveConnection);
    }

    [Fact]
    public async Task The_health_check_refuses_a_null_connection_rather_than_answering_about_nothing()
    {
        using var harness = OrassHarness.With(OrassHarness.ApiSettings());

        var act = async () => await harness.Adapter.CheckHealthAsync(null!, default);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── What the adapter claims, and what it deliberately does not ─────────────────────────

    [Fact]
    public void It_claims_the_policy_and_claim_ports_and_not_the_product_one()
    {
        using var harness = OrassHarness.With(OrassHarness.ApiSettings());

        // The two ASS-06's story names: « les souscriptions et sinistres arrivent directement dans
        // son back-office ».
        harness.Adapter.Should().BeAssignableTo<IInsurancePolicyPort>();
        harness.Adapter.Should().BeAssignableTo<IInsuranceClaimPort>();

        // Not the product port. Pricing and eligibility belong to ASS-02/ASS-03, and beyond scope
        // there are two independent reasons that happen to agree: on the bordereau carrier a
        // one-way file deposit can never quote anything (so the silence means NEVER, as in
        // PerfectVisionAdapter), and on the API carrier nothing tells us such an operation exists
        // (so it means "no evidence", as in AmplitudeCapabilityMatrix's ReadKycLevel). Declaring a
        // price we cannot obtain would have ASS-03 quote a premium at a counter from an operation
        // we have never seen.
        harness.Adapter.Should().NotBeAssignableTo<IInsuranceProductPort>();
    }

    [Fact]
    public void It_claims_no_core_banking_port()
    {
        using var harness = OrassHarness.With(OrassHarness.ApiSettings());

        // It implements ICbsAdapter — that is how this module resolves ANY adapter, insurance
        // included, since there is no IInsuranceAdapter and ResolveAdapter looks up a keyed
        // ICbsAdapter by kind. The interface's name is a legacy of the core-banking-first design;
        // the core banking PORTS are a different matter and are none of an insurer's business.
        harness.Adapter.Should().BeAssignableTo<ICbsAdapter>();

        harness.Adapter.Should().NotBeAssignableTo<ICbsCustomerPort>();
        harness.Adapter.Should().NotBeAssignableTo<ICbsAccountPort>();
        harness.Adapter.Should().NotBeAssignableTo<ICbsLoanPort>();
        harness.Adapter.Should().NotBeAssignableTo<ICbsTransactionPort>();
        harness.Adapter.Should().NotBeAssignableTo<ICbsKycLevelPort>();
    }

    // ── The deliverables of a blocked chantier ─────────────────────────────────────────────

    [Fact]
    public void The_six_questions_are_carried_in_the_code_in_the_order_they_unblock_work()
    {
        // Pinned because the list IS the deliverable of a blocked chantier: it is what a
        // procurement conversation needs, and a header comment that drifts from the class it
        // documents is a header comment nobody forwards.
        OrassSpecification.OpenQuestions.Should().HaveCount(6);

        OrassSpecification.OpenQuestions[0].Should().Contain("agreement");
        OrassSpecification.OpenQuestions[1].Should().Contain("operation catalogue");
        OrassSpecification.OpenQuestions[2].Should().Contain("bordereau layout");
        OrassSpecification.OpenQuestions[3].Should().Contain("acknowledgement");
        OrassSpecification.OpenQuestions[4].Should().Contain("apporteur");
        OrassSpecification.OpenQuestions[5].Should().Contain("test environment");
    }

    [Fact]
    public void The_two_carriers_are_two_separate_asks_and_the_list_says_so()
    {
        // The structural point: an answer about the API unblocks nothing about the bordereaux, and
        // the reverse. A procurement conversation that treats them as one artefact comes back with
        // neither.
        OrassSpecification.MissingDocumentFor(OrassCarrier.ExternalApi)
            .Should().NotBe(OrassSpecification.MissingDocumentFor(OrassCarrier.BatchSocle));

        // And an unknown carrier names both rather than guessing which is on the critical path.
        var unknown = OrassSpecification.MissingDocumentFor(null);

        unknown.Should().Contain("Bancassurance");
        unknown.Should().Contain("bordereau");
    }

    [Fact]
    public void No_refusal_detail_ever_names_an_endpoint_a_field_or_a_record()
    {
        // The tripwire for the one mistake this whole chantier exists to avoid. An invented
        // identifier in a message is how an invented identifier reaches a mapper: somebody reads
        // the error, takes the name for documentation, and the guess becomes the contract.
        OrassBranch?[] branches = [OrassBranch.Iard, OrassBranch.Vie, null];
        OrassCarrier?[] carriers = [OrassCarrier.ExternalApi, OrassCarrier.BatchSocle, null];

        foreach (var operation in PortMethodNames())
        foreach (var branch in branches)
        foreach (var carrier in carriers)
        {
            var detail = OrassSpecification.RefusalDetail(operation, branch, carrier);

            detail.Should().NotContainAny(
                "POST", "GET /", "http://", "https://", ".csv", ".txt", ".xml",
                "Content-Type", "Authorization", "VARCHAR", "NUMERIC");

            // Nor a configured value of the tenant's own: the branch is useful in a journal row,
            // the apporteur code is not, and it would appear in every rejection detail for nothing.
            detail.Should().NotContain(OrassHarness.PlaceholderIntermediaryCode);
        }
    }

    // ── Invocation plumbing ─────────────────────────────────────────────────────────────────

    private static IEnumerable<string> PortMethodNames()
        => new[] { typeof(IInsurancePolicyPort), typeof(IInsuranceClaimPort) }
            .SelectMany(port => port.GetMethods())
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

    private static InsurancePolicyPayload Payload()
        => new(
            CrmCustomerId: Guid.Empty,
            CrmProductId: Guid.Empty,
            InsurerProductCode: "unused",
            EffectiveDate: new DateOnly(2026, 1, 1),
            PremiumAmount: 0m,
            Currency: "XOF",
            Periodicity: PremiumPeriodicity.Annual,
            Beneficiaries: [],
            LinkedLoanReference: null,
            ConsentEvidenceRef: null);

    private static IdempotencyKey Key() => new("orass-test-key");

    /// <summary>
    /// Calls one port method by name with harmless arguments and hands back the result as the
    /// non-generic base.
    ///
    /// <para>
    /// The arguments are deliberately uninteresting: this adapter reads none of them, and a fixture
    /// built to look realistic would suggest the refusal depended on the payload. Both
    /// <c>IntegrationResult</c> and <c>IntegrationResult&lt;T&gt;</c> are reachable as the base
    /// type, so one helper covers writes and reads alike.
    /// </para>
    /// </summary>
    private static async Task<IntegrationResult> InvokeAsync(object adapter, string methodName)
    {
        var method = adapter.GetType().GetMethods()
            .Single(m => m.Name == methodName && !m.IsSpecialName);

        var arguments = method.GetParameters().Select(ArgumentFor).ToArray();

        var task = (Task)method.Invoke(adapter, arguments)!;
        await task;

        var value = task.GetType().GetProperty("Result")!.GetValue(task);

        return (IntegrationResult)value!;
    }

    private static object? ArgumentFor(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;

        if (type == typeof(CancellationToken)) return CancellationToken.None;
        if (type == typeof(IdempotencyKey)) return Key();
        if (type == typeof(ExternalId)) return new ExternalId("ORASS-TEST");
        if (type == typeof(InsurancePolicyPayload)) return Payload();
        if (type == typeof(string)) return "unused";

        // Reference payloads: null is safe precisely because the adapter refuses before reading
        // anything, and a test that had to build one would be asserting about the payload instead.
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
