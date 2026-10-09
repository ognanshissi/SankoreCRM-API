namespace Sankore.Modules.Integration.Tests.Adapters.PerfectVision;

using System.Reflection;
using FluentAssertions;
using Sankore.Modules.Integration.Adapters.PerfectVision;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-28, criterion 1 — the ports are in place and every one of them refuses, naming the
/// document that has not arrived.
///
/// <para>
/// What these tests defend is not a behaviour, it is a decision: that an adapter whose
/// specification is missing must say so rather than guess. M02's biometry client is the
/// counter-example the repository already paid for — hand-written wire records against a document
/// nobody had read, every field mapping to null, every KYC file stuck in <c>Verifying</c> against
/// a service that answered perfectly. A guessed batch layout fails more quietly still: the file is
/// deposited, the transfer succeeds, and the command waits forever for an acknowledgement that
/// Perfect Vision will never send because it could not read what we sent.
/// </para>
/// </summary>
public sealed class PerfectVisionAdapterTests
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
    /// <c>GetBalanceAsync</c> is excluded here and asserted in full below: it is the one method
    /// with a decision of its own, and criterion 2 gives it two different honest answers.
    /// </para>
    /// </summary>
    public static TheoryData<string> PortMethods()
    {
        var data = new TheoryData<string>();

        foreach (var name in PortMethodNames().Where(n => n != nameof(ICbsAccountPort.GetBalanceAsync)))
            data.Add(name);

        return data;
    }

    [Theory]
    [MemberData(nameof(PortMethods))]
    public async Task Every_port_method_refuses_and_names_the_missing_specification(string methodName)
    {
        using var harness = PerfectVisionHarness.With(new PerfectVisionSettings());

        var result = await InvokeAsync(harness.Adapter, methodName);

        result.IsFailure.Should().BeTrue($"{methodName} cannot be served without the specification");
        result.Code.Should().Be(IntegrationErrors.AdapterSpecificationPending);

        // Technical and never Transient: the family is what decides what the platform does next,
        // and retrying with backoff cannot obtain a vendor document. Technical parks the command
        // and alerts the administrator, which is the action that can actually move this forward.
        result.Family.Should().Be(ErrorFamily.Technical);
        result.IsRetryable.Should().BeFalse();

        // The operator-facing half. An error that said "not implemented" would send someone to
        // raise a defect and someone else to look for the half-written method; this one says the
        // document is missing and where that is recorded.
        result.Detail.Should().NotBeNullOrWhiteSpace();
        result.Detail.Should().Contain(PerfectVisionSpecification.MissingDocument);
        result.Detail.Should().Contain(PerfectVisionSpecification.PlanReference);
        result.Detail.Should().NotContain("not implemented");
    }

    [Fact]
    public async Task The_balance_read_goes_through_the_view_branch_when_a_view_is_configured()
    {
        using var harness = PerfectVisionHarness.With(
            new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" });

        var result = await harness.Adapter.GetBalanceAsync(new ExternalId("ACC-1"), default);

        // The installation can serve a live balance; what is missing is the view's columns. So the
        // specification code, not a capability refusal — and IntegrationModuleFacade logs it and
        // falls back to the snapshot, which is degraded and labelled rather than wrong.
        result.IsFailure.Should().BeTrue();
        result.Code.Should().Be(IntegrationErrors.AdapterSpecificationPending);
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Detail.Should().Contain(PerfectVisionSpecification.PlanReference);
    }

    [Fact]
    public async Task The_balance_read_says_it_cannot_serve_a_live_balance_when_there_is_no_view()
    {
        using var harness = PerfectVisionHarness.With(new PerfectVisionSettings());

        var result = await harness.Adapter.GetBalanceAsync(new ExternalId("ACC-1"), default);

        // A different answer on purpose, and the honest one: an installation without a view will
        // never serve a live balance however complete the file specification becomes. Nothing is
        // being waited for, so the code is not the specification one.
        result.Code.Should().Be(IntegrationErrors.CapabilityNotSupported);
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Detail.Should().Contain("snapshot", "the caller is told where the figure comes from");
    }

    [Fact]
    public void The_adapter_answers_for_its_own_kind()
    {
        using var harness = PerfectVisionHarness.With(new PerfectVisionSettings());

        // IntegrationAdapterResolver keys on connection.Kind.ToString() and nothing else.
        harness.Adapter.Kind.Should().Be(IntegrationKind.PerfectVision);
    }

    [Fact]
    public void The_matrix_is_read_from_the_tenants_own_connection()
    {
        using var withView = PerfectVisionHarness.With(
            new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" });

        using var withoutView = PerfectVisionHarness.With(new PerfectVisionSettings());

        withView.Adapter.Capabilities.Supports(IntegrationCapability.ReadBalance)
            .Should().BeTrue("this installation exposes a read-only view");

        withoutView.Adapter.Capabilities.Supports(IntegrationCapability.ReadBalance)
            .Should().BeFalse("this one does not, and the matrix must say so");
    }

    [Fact]
    public void A_connection_that_is_not_active_still_yields_a_matrix()
    {
        // Deliberate, and the opposite of every other adapter's binding. A Perfect Vision
        // connection can never be activated — its health check cannot pass until the specification
        // arrives — so filtering on IsActive would make the matrix permanently empty and
        // criterion 3 undeliverable. Reading an inactive row grants nothing: every port refuses
        // regardless.
        using var harness = PerfectVisionHarness.With(
            new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" });

        harness.Adapter.Capabilities.Modes.Should().NotBeEmpty();
    }

    [Fact]
    public void A_tenant_with_no_connection_gets_the_narrow_matrix_rather_than_an_exception()
    {
        using var harness = PerfectVisionHarness.WithNoConnection();

        var matrix = harness.Adapter.Capabilities;

        matrix.Supports(IntegrationCapability.ReadBalance).Should().BeFalse();
        matrix.Supports(IntegrationCapability.CreateCustomer).Should().BeTrue();
    }

    [Fact]
    public void The_relay_mode_changes_nothing_about_the_matrix()
    {
        // The same installation reached through the on-premise agent (INT-26) is the same
        // installation. If the carrier changed the matrix, a tenant that moved behind a relay
        // would lose buttons for a reason that has nothing to do with what its CBS can do.
        using var direct = PerfectVisionHarness.With(
            new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" });

        using var relayed = PerfectVisionHarness.With(
            new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" },
            IntegrationMode.Relay);

        relayed.Adapter.Capabilities.ToDictionary()
            .Should().BeEquivalentTo(direct.Adapter.Capabilities.ToDictionary());
    }

    // ── Health (INT-03) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_health_check_answers_unhealthy_instead_of_throwing()
    {
        using var harness = PerfectVisionHarness.With(new PerfectVisionSettings());

        var connection = PerfectVisionHarness.ConnectionCarrying(new PerfectVisionSettings());

        var health = await harness.Adapter.CheckHealthAsync(connection, default);

        // Called from the activation screen and from the `integration` health check. A throwing
        // adapter would turn a known supplier dependency into a 500 that reads like an outage of
        // ours.
        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.AdapterSpecificationPending);
        health.Detail.Should().Contain(PerfectVisionSpecification.PlanReference);

        // No latency, because nothing was called. Reporting zero would put this row in the
        // activation screen's latency column next to systems that actually answered.
        health.Latency.Should().BeNull();
        health.CheckedAt.Should().NotBe(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task A_failed_health_check_is_what_keeps_the_connection_inactive()
    {
        using var harness = PerfectVisionHarness.With(new PerfectVisionSettings());

        var connection = PerfectVisionHarness.ConnectionCarrying(new PerfectVisionSettings());

        var health = await harness.Adapter.CheckHealthAsync(connection, default);
        connection.RecordHealth(health, TimeProvider.System);

        var activation = connection.Activate(Guid.NewGuid(), TimeProvider.System);

        // The fail-closed outcome, and the reason the health answer matters more here than
        // anywhere else: an active Perfect Vision connection would have the dispatcher queue
        // customer creations and KYC writes that no code in this repository can turn into a file.
        activation.IsSuccess.Should().BeFalse();
        activation.Error.Should().Be(IntegrationErrors.ConnectionNotHealthy);
        connection.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Settings_of_the_wrong_shape_are_reported_as_a_configuration_fault()
    {
        using var harness = PerfectVisionHarness.With(new PerfectVisionSettings());

        // A row whose settings are not Perfect Vision's. Defence in depth — the aggregate refuses
        // the mismatch on write — but the two faults send an administrator to different screens,
        // and collapsing them would send someone to procurement over a mistake they can fix
        // themselves.
        var foreign = PerfectVisionHarness.ConnectionCarrying(
            new TemenosSettings { BaseUrl = "https://example.invalid" });

        var health = await harness.Adapter.CheckHealthAsync(foreign, default);

        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
        health.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);
    }

    [Fact]
    public async Task The_health_check_refuses_a_null_connection_rather_than_answering_about_nothing()
    {
        using var harness = PerfectVisionHarness.With(new PerfectVisionSettings());

        var act = async () => await harness.Adapter.CheckHealthAsync(null!, default);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── What the adapter deliberately does not claim ────────────────────────────────────────

    [Fact]
    public void The_queries_a_batch_system_cannot_answer_are_not_claimed_as_interfaces()
    {
        using var harness = PerfectVisionHarness.With(new PerfectVisionSettings());

        // Not implementing an interface says "never"; implementing it and refusing says "not yet".
        // A batch-file CBS answers no query at all, so transaction history and the KYC tier are
        // read from the INT-21 snapshot — and CbsSnapshotProjector branches on
        // Supports(ReadKycLevel) to record the difference rather than pretend it has the figure.
        harness.Adapter.Should().NotBeAssignableTo<ICbsTransactionPort>();
        harness.Adapter.Should().NotBeAssignableTo<ICbsKycLevelPort>();

        harness.Adapter.Should().BeAssignableTo<ICbsCustomerPort>();
        harness.Adapter.Should().BeAssignableTo<ICbsAccountPort>();
        harness.Adapter.Should().BeAssignableTo<ICbsLoanPort>();
    }

    [Fact]
    public void The_three_questions_for_the_vendor_are_carried_in_the_code()
    {
        // Pinned because the list is the deliverable of a blocked chantier: it is what a
        // procurement conversation needs, and a header comment that drifts from the class it
        // documents is a header comment nobody forwards.
        PerfectVisionSpecification.OpenQuestions.Should().HaveCount(3);

        PerfectVisionSpecification.OpenQuestions[0].Should().Contain("layout");
        PerfectVisionSpecification.OpenQuestions[1].Should().Contain("acknowledgement");
        PerfectVisionSpecification.OpenQuestions[2].Should().Contain("columns");
    }

    [Fact]
    public void No_refusal_detail_ever_names_a_field_a_column_or_a_record()
    {
        using var harness = PerfectVisionHarness.With(new PerfectVisionSettings());

        // The tripwire for the one mistake this whole chantier exists to avoid. An invented
        // identifier in a message is how an invented identifier reaches a mapper: somebody reads
        // the error, takes the name for documentation, and the guess becomes the contract.
        foreach (var operation in PortMethodNames())
        {
            var detail = PerfectVisionSpecification.RefusalDetail(operation);

            detail.Should().NotContainAny("SELECT", "INSERT", "VARCHAR", "NUMERIC", ".csv", ".txt");
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
    /// The arguments are deliberately uninteresting: this adapter reads none of them, and a
    /// fixture built to look realistic would suggest the refusal depended on the payload. Both
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
        if (type == typeof(IdempotencyKey)) return new IdempotencyKey("pv-test-key");
        if (type == typeof(ExternalId)) return new ExternalId("PV-TEST");
        if (type == typeof(KycLevel)) return KycLevel.Simplified;
        if (type == typeof(decimal)) return 0m;
        if (type == typeof(string)) return "unused";

        // Reference payloads: null is safe precisely because the adapter refuses before reading
        // anything, and a test that had to build one would be asserting about the payload instead.
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
