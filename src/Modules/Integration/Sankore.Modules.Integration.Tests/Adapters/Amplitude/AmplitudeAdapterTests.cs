namespace Sankore.Modules.Integration.Tests.Adapters.Amplitude;

using System.Reflection;
using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Amplitude;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-31, criterion 1 — the ports are in place and every one of them refuses, naming the artefact
/// this installation's release needs from SBS.
///
/// <para>
/// What these tests defend is not a behaviour, it is a decision: that an adapter whose interface
/// contract is missing must say so rather than guess. M02's biometry client is the counter-example
/// the repository already paid for — hand-written wire records against a document nobody had read,
/// every field mapping to null, every KYC file stuck in <c>Verifying</c> against a service that
/// answered perfectly. A guessed pre-Up batch layout fails more quietly still: the file is
/// deposited, the transfer succeeds, and the command waits out its <c>AckTimeoutHours</c> for an
/// acknowledgement Amplitude will never send because it could not read what we sent.
/// </para>
/// </summary>
public sealed class AmplitudeAdapterTests
{
    /// <summary>
    /// Every method of every port this adapter implements, discovered rather than listed.
    ///
    /// <para>
    /// Reflection on purpose. A hand-written list of ten calls is a list that goes stale the day a
    /// port grows an eleventh method — exactly the day it matters, because a new port method
    /// returning a default <c>IntegrationResult</c> would be the only silent success in an adapter
    /// whose whole contract is to refuse.
    /// </para>
    ///
    /// <para>
    /// The three reads are excluded here and asserted in full below: they are the methods with a
    /// decision of their own, and criterion 2 gives each of them two different honest answers
    /// depending on the release.
    /// </para>
    /// </summary>
    public static TheoryData<string> WriteMethods()
    {
        var data = new TheoryData<string>();

        foreach (var name in PortMethodNames().Except(ReadMethodNames, StringComparer.Ordinal))
            data.Add(name);

        return data;
    }

    private static readonly string[] ReadMethodNames =
    [
        nameof(ICbsAccountPort.GetAccountsAsync),
        nameof(ICbsAccountPort.GetBalanceAsync),
        nameof(ICbsLoanPort.GetLoansAsync),
    ];

    [Theory]
    [MemberData(nameof(WriteMethods))]
    public async Task Every_write_refuses_and_names_the_missing_contract(string methodName)
    {
        using var harness = AmplitudeHarness.With(AmplitudeVersion.Legacy);

        var result = await InvokeAsync(harness.Adapter, methodName);

        result.IsFailure.Should().BeTrue($"{methodName} cannot be served without the contract");
        result.Code.Should().Be(IntegrationErrors.AdapterSpecificationPending);

        // Technical and never Transient: the family is what decides what the platform does next,
        // and retrying with backoff cannot obtain a supplier document. Technical parks the command
        // and alerts the administrator, which is the action that can actually move this forward.
        result.Family.Should().Be(ErrorFamily.Technical);
        result.IsRetryable.Should().BeFalse();

        // The operator-facing half. An error that said "not implemented" would send someone to
        // raise a defect and someone else to look for the half-written method; this one says which
        // document is missing, who has it, and where the block is recorded.
        result.Detail.Should().NotBeNullOrWhiteSpace();
        result.Detail.Should().Contain(AmplitudeSpecification.PlanReference);
        result.Detail.Should().Contain(AmplitudeSpecification.Supplier);
        result.Detail.Should().NotContain("not implemented");
    }

    [Theory]
    [InlineData(AmplitudeVersion.Up)]
    [InlineData(AmplitudeVersion.Legacy)]
    public async Task A_refusal_names_the_artefact_this_release_needs_and_not_the_other_one(
        AmplitudeVersion version)
    {
        using var harness = AmplitudeHarness.With(version);

        var result = await harness.Adapter.CreateCustomerAsync(null!, new IdempotencyKey("k"), default);

        // The one thing this adapter does that Perfect Vision's cannot: it knows which of two
        // separate asks of SBS is the one blocking THIS installation. An operator who forwards
        // "we need the Amplitude specification" gets a conversation; one who forwards the sentence
        // below gets a document.
        result.Detail.Should().Contain(AmplitudeSpecification.MissingDocumentFor(version));

        var other = version == AmplitudeVersion.Up ? AmplitudeVersion.Legacy : AmplitudeVersion.Up;

        result.Detail.Should().NotContain(AmplitudeSpecification.MissingDocumentFor(other));
    }

    [Theory]
    [InlineData(nameof(ICbsAccountPort.GetAccountsAsync))]
    [InlineData(nameof(ICbsAccountPort.GetBalanceAsync))]
    [InlineData(nameof(ICbsLoanPort.GetLoansAsync))]
    public async Task An_up_installation_reports_a_read_as_waiting_on_the_catalogue(string methodName)
    {
        using var harness = AmplitudeHarness.With(AmplitudeVersion.Up, IntegrationMode.Api);

        var result = await InvokeAsync(harness.Adapter, methodName);

        // The installation can serve the read; what is missing is the service catalogue. So the
        // specification code, not a capability refusal — and IntegrationModuleFacade logs it and
        // falls back to the snapshot, which is degraded and labelled rather than wrong.
        result.IsFailure.Should().BeTrue();
        result.Code.Should().Be(IntegrationErrors.AdapterSpecificationPending);
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Detail.Should().Contain(AmplitudeSpecification.PlanReference);
    }

    [Theory]
    [InlineData(nameof(ICbsAccountPort.GetAccountsAsync))]
    [InlineData(nameof(ICbsAccountPort.GetBalanceAsync))]
    [InlineData(nameof(ICbsLoanPort.GetLoansAsync))]
    public async Task A_pre_up_installation_says_it_cannot_serve_a_live_read_at_all(string methodName)
    {
        using var harness = AmplitudeHarness.With(AmplitudeVersion.Legacy);

        var result = await InvokeAsync(harness.Adapter, methodName);

        // A different answer on purpose, and the honest one: a pre-Up installation will never
        // serve a live read however complete the file layout becomes. Nothing is being waited for,
        // so the code is not the specification one — and an operator must not forward this to SBS.
        result.Code.Should().Be(IntegrationErrors.CapabilityNotSupported);
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Detail.Should().Contain("snapshot", "the caller is told where the figure comes from");
        result.Detail.Should().NotContain(AmplitudeSpecification.Supplier);
    }

    [Fact]
    public void The_adapter_answers_for_its_own_kind()
    {
        using var harness = AmplitudeHarness.With();

        // IntegrationAdapterResolver keys on connection.Kind.ToString() and nothing else.
        harness.Adapter.Kind.Should().Be(IntegrationKind.Amplitude);
    }

    // ── The matrix, read from the tenant's own row (criterion 3) ─────────────────────────────

    [Fact]
    public void The_matrix_is_read_from_the_connection_it_is_handed()
    {
        // Was two harnesses compared through a parameterless Capabilities property, which could
        // only ever express "this KIND's matrix". Same purpose — INT-31's criterion 3, the matrix
        // follows the release installed at the IMF — asserted the way the contract now states it:
        // ONE adapter, two rows, two answers.
        using var harness = AmplitudeHarness.With();

        var up = AmplitudeHarness.ConnectionCarrying(
            AmplitudeHarness.Settings(AmplitudeVersion.Up), IntegrationMode.Api);

        var legacy = AmplitudeHarness.ConnectionCarrying(
            AmplitudeHarness.Settings(AmplitudeVersion.Legacy));

        harness.Adapter.CapabilitiesFor(up).IsRealTime(IntegrationCapability.CreateCustomer)
            .Should().BeTrue("this installation runs Up and is reached by API");

        harness.Adapter.CapabilitiesFor(legacy).IsRealTime(IntegrationCapability.CreateCustomer)
            .Should().BeFalse("this one exchanges files, and the matrix must say so");
    }

    [Fact]
    public void The_matrix_reads_the_connections_mode_as_well_as_its_release()
    {
        using var harness = AmplitudeHarness.With();

        var api = AmplitudeHarness.ConnectionCarrying(
            AmplitudeHarness.Settings(AmplitudeVersion.Up), IntegrationMode.Api);

        var batch = AmplitudeHarness.ConnectionCarrying(
            AmplitudeHarness.Settings(AmplitudeVersion.Up), IntegrationMode.Batch);

        // Both rows say Up; only the mode differs. If this test fails, the adapter has stopped
        // reading the mode column — and the symptom in production would be a screen offering live
        // writes on a connection whose commands are deposited in a file.
        harness.Adapter.CapabilitiesFor(api).ModeOf(IntegrationCapability.CreateCustomer)
            .Should().Be(CapabilityMode.RealTime);

        harness.Adapter.CapabilitiesFor(batch).ModeOf(IntegrationCapability.CreateCustomer)
            .Should().Be(CapabilityMode.Batch);

        // And the reads are unaffected by the mode, which is the asymmetry the facade relies on.
        harness.Adapter.CapabilitiesFor(batch).IsRealTime(IntegrationCapability.ReadBalance)
            .Should().BeTrue();
    }

    [Fact]
    public void A_connection_that_is_not_active_still_yields_a_matrix()
    {
        // Deliberate, and the opposite of every other adapter's binding. An Amplitude connection
        // can never be activated — its health check cannot pass until the contract arrives — so
        // refusing an inactive row would make the matrix permanently empty and criterion 3
        // undeliverable. Answering for one grants nothing: every port refuses regardless.
        using var harness = AmplitudeHarness.With();

        // ConnectionCarrying never activates the row it builds, which is the state under test.
        var inactive = AmplitudeHarness.ConnectionCarrying(
            AmplitudeHarness.Settings(AmplitudeVersion.Up), IntegrationMode.Api);

        inactive.IsActive.Should().BeFalse("otherwise this test is about an active connection");

        harness.Adapter.CapabilitiesFor(inactive).Modes.Should().NotBeEmpty();
    }

    [Fact]
    public void A_row_carrying_another_kinds_settings_gets_the_narrow_matrix_rather_than_an_exception()
    {
        // Was "a tenant with no connection": the matrix used to be looked up by the adapter, so
        // "no row" was the narrowest input it could be given. CapabilitiesFor takes a row, so the
        // narrowest input is now a row whose settings it cannot read as Amplitude's — the `as` in
        // CapabilitiesFor, reached by a screen only asking what is available.
        using var harness = AmplitudeHarness.With();

        var foreign = AmplitudeHarness.ConnectionCarrying(
            new TemenosSettings { BaseUrl = "https://example.invalid" }, IntegrationMode.Api);

        var matrix = harness.Adapter.CapabilitiesFor(foreign);

        // Unreadable settings mean no release, and the matrix narrows on that alone — the MODE is
        // still read, from a column a settings mismatch cannot corrupt: the writes stay, because
        // they are a property of the product a screen may legitimately ask about, and nothing is
        // declared live.
        matrix.Supports(IntegrationCapability.CreateCustomer).Should().BeTrue();
        matrix.ModeOf(IntegrationCapability.CreateCustomer).Should().Be(CapabilityMode.Batch);
        matrix.Supports(IntegrationCapability.ReadBalance).Should().BeFalse();
    }

    // ── Health (INT-03) ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AmplitudeVersion.Up, IntegrationMode.Api)]
    [InlineData(AmplitudeVersion.Up, IntegrationMode.Batch)]
    [InlineData(AmplitudeVersion.Legacy, IntegrationMode.Batch)]
    public async Task The_health_check_answers_unhealthy_instead_of_throwing(
        AmplitudeVersion version, IntegrationMode mode)
    {
        using var harness = AmplitudeHarness.With(version, mode);

        var connection = AmplitudeHarness.ConnectionCarrying(
            AmplitudeHarness.Settings(version), mode);

        var health = await harness.Adapter.CheckHealthAsync(connection, default);

        // Called from the activation screen and from the `integration` health check. A throwing
        // adapter would turn a known supplier dependency into a 500 that reads like an outage of
        // ours. Asserted for every COHERENT combination, so that no release and no carrier can
        // quietly become healthy.
        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.AdapterSpecificationPending);
        health.Detail.Should().Contain(AmplitudeSpecification.MissingDocumentFor(version));
        health.Detail.Should().Contain(AmplitudeSpecification.PlanReference);

        // No latency, because nothing was called. Reporting zero would put this row in the
        // activation screen's latency column next to systems that actually answered.
        health.Latency.Should().BeNull();
        health.CheckedAt.Should().NotBe(DateTimeOffset.MinValue);
    }

    [Theory]
    [InlineData(AmplitudeVersion.Up, IntegrationMode.Api)]
    [InlineData(AmplitudeVersion.Up, IntegrationMode.Batch)]
    [InlineData(AmplitudeVersion.Legacy, IntegrationMode.Batch)]
    public async Task A_failed_health_check_is_what_keeps_the_connection_inactive(
        AmplitudeVersion version, IntegrationMode mode)
    {
        using var harness = AmplitudeHarness.With(version, mode);

        var connection = AmplitudeHarness.ConnectionCarrying(
            AmplitudeHarness.Settings(version), mode);

        var health = await harness.Adapter.CheckHealthAsync(connection, default);
        connection.RecordHealth(health, TimeProvider.System);

        var activation = connection.Activate(Guid.NewGuid(), TimeProvider.System);

        // THE STRUCTURAL LOCK, and for Amplitude it carries more weight than for any other blocked
        // adapter. ExecuteIntegrationCommandHandler routes on connection.Mode BEFORE it resolves an
        // adapter: on an ACTIVE Batch connection the commands would never reach this class, so none
        // of its refusals would ever be consulted — the shared batch writer would deposit a file in
        // a layout nobody has specified, the SFTP transfer would report success, and every command
        // would wait out its AckTimeoutHours for an acknowledgement that cannot come. The health
        // gate is the only thing standing in that path, which is why it is pinned for the Batch
        // carrier as well as the API one.
        activation.IsSuccess.Should().BeFalse();
        activation.Error.Should().Be(IntegrationErrors.ConnectionNotHealthy);
        connection.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Settings_of_the_wrong_shape_are_reported_as_a_configuration_fault()
    {
        using var harness = AmplitudeHarness.With();

        // A row whose settings are not Amplitude's. Defence in depth — the aggregate refuses the
        // mismatch on write — but the two faults send an administrator to different screens, and
        // collapsing them would send someone to procurement over a mistake they can fix themselves.
        var foreign = AmplitudeHarness.ConnectionCarrying(
            new TemenosSettings { BaseUrl = "https://example.invalid" });

        var health = await harness.Adapter.CheckHealthAsync(foreign, default);

        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
        health.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);
    }

    [Theory]
    [InlineData(IntegrationMode.Api)]
    public async Task A_release_that_contradicts_the_mode_is_its_own_distinct_fault(
        IntegrationMode mode)
    {
        // Api only. Relay was the second incoherent mode until L8, when the dispatcher and the
        // scheduled batch generation were made to read one definition of "leaves in a file"
        // (OutboundBatchCarrier): a pre-Up installation behind a relay agent now produces and
        // deposits its files, so it is a legitimate configuration and reporting it as a fault
        // would send an administrator to change something that works. The coherent-Relay case is
        // asserted by the test below.
        using var harness = AmplitudeHarness.With();

        var connection = AmplitudeHarness.ConnectionCarrying(
            AmplitudeHarness.Settings(AmplitudeVersion.Legacy), mode);

        var health = await harness.Adapter.CheckHealthAsync(connection, default);

        // THE POINT OF THIS TEST IS THE DISTINCTION, not the failure. All three faults end in
        // Unhealthy, so an assertion on IsHealthy alone would pass with the three collapsed into
        // one message — and an administrator who could have fixed a dropdown in ten seconds would
        // instead be told to wait for a document from SBS that would not help them.
        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
        health.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);
        health.Detail.Should().NotContain(AmplitudeSpecification.Supplier);

        // And it names what to change, in both directions.
        health.Detail.Should().Contain(nameof(IntegrationMode.Batch));
        health.Detail.Should().Contain(nameof(AmplitudeVersion.Up));
    }

    [Fact]
    public async Task A_pre_up_release_behind_a_relay_agent_is_not_reported_as_a_fault()
    {
        // The other half of the restriction above, and the reason it is a restriction rather than
        // a deletion: narrowing the theory alone would have left nothing asserting that Relay is
        // now accepted, so a later change could silently make it a fault again.
        using var harness = AmplitudeHarness.With();

        var connection = AmplitudeHarness.ConnectionCarrying(
            AmplitudeHarness.Settings(AmplitudeVersion.Legacy), IntegrationMode.Relay);

        var health = await harness.Adapter.CheckHealthAsync(connection, default);

        // Still Unhealthy — the ORASS-shaped structural lock: no Amplitude connection can be
        // activated until SBS delivers. But the reason must be the missing contract, NOT a
        // configuration fault, because there is nothing here for an administrator to correct.
        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.AdapterSpecificationPending);
        health.Detail.Should().NotContain(IntegrationErrors.SettingsInvalid);
    }

    [Fact]
    public async Task The_coherence_check_reads_the_connection_it_is_given_and_not_the_stored_row()
    {
        // The health check runs BEFORE activation, on the connection the administrator is editing,
        // which may not be the row this adapter would bind to. Here the bound row is a coherent
        // pre-Up/Batch one and the argument is an incoherent pre-Up/Api one: the answer must follow
        // the argument, or an administrator would test one connection and be told about another.
        using var harness = AmplitudeHarness.With(AmplitudeVersion.Legacy, IntegrationMode.Batch);

        var edited = AmplitudeHarness.ConnectionCarrying(
            AmplitudeHarness.Settings(AmplitudeVersion.Legacy), IntegrationMode.Api);

        var health = await harness.Adapter.CheckHealthAsync(edited, default);

        health.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
    }

    [Fact]
    public async Task The_health_check_refuses_a_null_connection_rather_than_answering_about_nothing()
    {
        using var harness = AmplitudeHarness.With();

        var act = async () => await harness.Adapter.CheckHealthAsync(null!, default);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── What the adapter deliberately does not claim ────────────────────────────────────────

    [Fact]
    public void The_two_ports_neither_release_serves_are_not_claimed_as_interfaces()
    {
        using var harness = AmplitudeHarness.With(AmplitudeVersion.Up, IntegrationMode.Api);

        // One class serves both releases, so its interface set is the UNION over releases and
        // cannot express a per-release difference — the matrix is the only place such a difference
        // can live. The two ports left unclaimed are therefore the ones NEITHER release serves:
        // transaction history and monthly flow (INT-13/INT-22, in no criterion of INT-31) and the
        // KYC tier read (impossible on a batch CBS, unevidenced on Up).
        harness.Adapter.Should().NotBeAssignableTo<ICbsTransactionPort>();
        harness.Adapter.Should().NotBeAssignableTo<ICbsKycLevelPort>();

        harness.Adapter.Should().BeAssignableTo<ICbsCustomerPort>();
        harness.Adapter.Should().BeAssignableTo<ICbsAccountPort>();
        harness.Adapter.Should().BeAssignableTo<ICbsLoanPort>();
    }

    [Fact]
    public void The_questions_for_sbs_are_carried_in_the_code_and_ordered()
    {
        // Pinned because the list is the deliverable of a blocked chantier: it is what a
        // procurement conversation needs, and a header comment that drifts from the class it
        // documents is a header comment nobody forwards.
        AmplitudeSpecification.OpenQuestions.Should().HaveCount(5);

        // The order is the content. Question 1 decides which ask is on the critical path, 2 is the
        // Up catalogue, 3 and 4 are the pre-Up pair — layout then acknowledgement, because a file
        // that cannot be closed is worse than a file that cannot be written — and 5 is the
        // environment, last, because obtained first it has nothing to receive.
        AmplitudeSpecification.OpenQuestions[0].Should().Contain("releases are in scope");
        AmplitudeSpecification.OpenQuestions[1].Should().Contain("catalogue");
        AmplitudeSpecification.OpenQuestions[2].Should().Contain("layout");
        AmplitudeSpecification.OpenQuestions[3].Should().Contain("acknowledgement");
        AmplitudeSpecification.OpenQuestions[4].Should().Contain("test environment");
    }

    [Fact]
    public void The_two_asks_of_sbs_are_never_conflated_into_one_sentence()
    {
        var up = AmplitudeSpecification.MissingDocumentFor(AmplitudeVersion.Up);
        var legacy = AmplitudeSpecification.MissingDocumentFor(AmplitudeVersion.Legacy);

        // Two separate asks, of different teams, about different releases: an answer to one
        // unblocks nothing about the other, so neither sentence may name the other's artefact.
        up.Should().NotBe(legacy);
        up.Should().NotContain("file layout");
        legacy.Should().NotContain("catalogue");

        // An unknown release names BOTH rather than guessing one. Naming the wrong artefact would
        // send a procurement conversation after a document that would not unblock the installation.
        var unknown = AmplitudeSpecification.MissingDocumentFor(null);

        unknown.Should().Contain("catalogue").And.Contain("file layout");
    }

    [Fact]
    public void No_refusal_detail_ever_names_a_service_a_field_or_a_record()
    {
        // The tripwire for the one mistake this whole chantier exists to avoid. An invented
        // identifier in a message is how an invented identifier reaches a mapper: somebody reads
        // the error, takes the name for documentation, and the guess becomes the contract.
        foreach (var operation in PortMethodNames())
        {
            foreach (var version in new AmplitudeVersion?[]
                     { AmplitudeVersion.Up, AmplitudeVersion.Legacy, null })
            {
                var detail = AmplitudeSpecification.RefusalDetail(operation, version);

                detail.Should().NotContainAny(
                    "/api", "http", "SELECT", "VARCHAR", "NUMERIC", ".csv", ".txt", ".xml");
            }
        }
    }

    // ── Invocation plumbing ─────────────────────────────────────────────────────────────────

    private static IEnumerable<string> PortMethodNames()
        => new[] { typeof(ICbsCustomerPort), typeof(ICbsAccountPort), typeof(ICbsLoanPort) }
            .SelectMany(port => port.GetMethods())
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

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
        if (type == typeof(IdempotencyKey)) return new IdempotencyKey("amplitude-test-key");
        if (type == typeof(ExternalId)) return new ExternalId("AMP-TEST");
        if (type == typeof(KycLevel)) return KycLevel.Simplified;
        if (type == typeof(decimal)) return 0m;
        if (type == typeof(string)) return "unused";

        // Reference payloads: null is safe precisely because the adapter refuses before reading
        // anything, and a test that had to build one would be asserting about the payload instead.
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
