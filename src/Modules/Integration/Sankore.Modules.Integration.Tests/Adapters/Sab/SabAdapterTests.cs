namespace Sankore.Modules.Integration.Tests.Adapters.Sab;

using System.Reflection;
using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Sab;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-32 — the ports are in place, the entity is guaranteed on every call, and every operation
/// refuses while naming the one thing that is missing for it.
///
/// <para>
/// What these tests defend is not a behaviour, it is a decision: that an adapter whose catalogue
/// is missing must say so rather than guess. M02's biometry client is the counter-example the
/// repository already paid for — hand-written wire records against a document nobody had read,
/// every field mapping to null, every KYC file stuck in <c>Verifying</c> against a service that
/// answered perfectly. A guessed Open SAB call fails worse than that, because its entity scoping
/// would be guessed too: the quiet outcome is not a write the CBS never accepted, it is a write
/// accepted by the WRONG INSTITUTION of a multi-IMF network, which looks like success from here.
/// </para>
/// </summary>
public sealed class SabAdapterTests
{
    /// <summary>
    /// Every method of every port this adapter implements, discovered rather than listed, minus
    /// the two ASS-05 ones that answer for a different reason and are asserted on their own.
    ///
    /// <para>
    /// Reflection on purpose. A hand-written list of twelve calls is a list that goes stale the
    /// day a port grows a thirteenth method — exactly the day it matters, because a new port
    /// method returning a default <c>IntegrationResult</c> would be the only silent success in an
    /// adapter whose whole contract is to refuse, and the only one reaching Open SAB with no
    /// entity.
    /// </para>
    /// </summary>
    public static TheoryData<string> InScopeMethods()
    {
        var data = new TheoryData<string>();

        foreach (var name in PortMethodNames().Where(n => !IsOutOfScope(n)))
            data.Add(name);

        return data;
    }

    [Theory]
    [MemberData(nameof(InScopeMethods))]
    public async Task Every_port_method_refuses_and_names_the_missing_catalogue(string methodName)
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        var result = await InvokeAsync(harness.Adapter, methodName);

        result.IsFailure.Should().BeTrue($"{methodName} cannot be served without the catalogue");
        result.Code.Should().Be(IntegrationErrors.AdapterSpecificationPending);

        // Technical and never Transient: the family is what decides what the platform does next,
        // and retrying with backoff cannot obtain a vendor document. Technical parks the command
        // and alerts the administrator, which is the action that can actually move this forward.
        result.Family.Should().Be(ErrorFamily.Technical);
        result.IsRetryable.Should().BeFalse();

        // The operator-facing half. An error that said "not implemented" would send someone to
        // raise a defect and someone else to look for the half-written method; this one says which
        // document is missing, from whom, and where that is recorded.
        result.Detail.Should().NotBeNullOrWhiteSpace();
        result.Detail.Should().Contain(SabSpecification.MissingDocument);
        result.Detail.Should().Contain(SabSpecification.PlanReference);
        result.Detail.Should().Contain("SBS", "the reader has to know who owns the blocker");
        result.Detail.Should().NotContain("not implemented");
    }

    // ── Criterion 2 at call time ────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(InScopeMethods))]
    public async Task No_port_method_can_be_reached_past_a_connection_with_no_entity(string methodName)
    {
        using var harness = SabHarness.With(SabHarness.UnscopedSettings());

        var result = await InvokeAsync(harness.Adapter, methodName);

        // THE FAILURE MODE THIS TEST EXISTS TO CATCH: an implementer removes the catalogue refusal
        // from one method — which is exactly how this adapter is meant to be finished, one method
        // at a time — and builds the call without the scope check, because the check was a line in
        // a sibling method rather than the only path to a result. On a CIF-style installation that
        // call lands on whatever entity Open SAB defaults to: a customer created for another
        // institution, or another institution's balance read at our counter, with no HTTP boundary
        // of ours crossed and nothing in any log to show for it.
        result.IsFailure.Should().BeTrue($"{methodName} must not proceed on an unscoped connection");
        result.Code.Should().Be(IntegrationErrors.SettingsInvalid);
        result.Family.Should().Be(ErrorFamily.Technical);
        result.IsRetryable.Should().BeFalse();

        // And it is reported as the tenant's own configuration, not as the pending catalogue: one
        // is a field on a screen the administrator already has open, the other is a procurement
        // conversation.
        result.Detail.Should().Contain("settings.entity");
        result.Detail.Should().NotContain(SabSpecification.MissingDocument);
    }

    [Fact]
    public async Task The_entity_is_refused_before_the_credential_and_before_the_catalogue()
    {
        using var unscopedAndUnkeyed = SabHarness.With(
            SabHarness.UnscopedSettings(), credentialStored: false);

        var result = await unscopedAndUnkeyed.Adapter.CreateCustomerAsync(
            null!, new IdempotencyKey("sab-test"), default);

        // The order is the order of a real call — scope it, authenticate it, build it — and the
        // first step is the one with the cross-institution consequence. It is also the ordering a
        // future transport must inherit: a credential resolved before the scope was established
        // is one tenant's key held while deciding whose data to ask for.
        result.Code.Should().Be(IntegrationErrors.SettingsInvalid);
        result.Detail.Should().Contain("settings.entity");

        // The vault is not even consulted: there is nothing to authenticate against an institution
        // we cannot name.
        unscopedAndUnkeyed.Secrets.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_api_key_is_its_own_fault_and_not_the_missing_catalogue()
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings(), credentialStored: false);

        var result = await harness.Adapter.GetBalanceAsync(new ExternalId("ACC-1"), default);

        // Criterion 1's deliverable half. "No API key is configured for this connection" is fixed
        // in a minute by the tenant's administrator; "no Open SAB catalogue" is a negotiation with
        // SBS. One code each, so the two never arrive at the same screen.
        result.Code.Should().Be(IntegrationErrors.CredentialMissing);
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Detail.Should().NotContain(SabSpecification.MissingDocument);
        result.Detail.Should().NotContain("settings.entity");
    }

    [Fact]
    public async Task The_premium_debit_of_ASS_05_says_it_is_not_supported_rather_than_pending()
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        var debit = await harness.Adapter.DebitAccountAsync(
            new ExternalId("ACC-1"), 1_000m, "XOF", "prime", new IdempotencyKey("sab-test"), default);

        var reversal = await harness.Adapter.ReverseDebitAsync(
            new ExternalId("ACC-1"), "REF-1", new IdempotencyKey("sab-test"), default);

        foreach (var result in new[] { debit, reversal })
        {
            // Nothing is being waited on for these: they are absent from the capability matrix
            // because ASS-05 has not brought them into scope, so the resolver refuses them before
            // the call and the catalogue would change nothing. Reporting them as pending would put
            // two operations nobody asked for into the list of things SBS is blocking.
            result.Code.Should().Be(IntegrationErrors.CapabilityNotSupported);
            result.Family.Should().Be(ErrorFamily.Technical);
            result.Detail.Should().NotContain(SabSpecification.MissingDocument);
        }
    }

    [Fact]
    public async Task A_tenant_with_no_SAB_connection_is_told_that_and_not_about_a_document()
    {
        using var harness = SabHarness.WithNoConnection();

        var result = await harness.Adapter.SetKycLevelAsync(
            new ExternalId("CUST-1"), KycLevel.Simplified, new IdempotencyKey("sab-test"), default);

        // Three different screens, three different answers: nothing configured, configured wrong,
        // configured right but blocked on SBS.
        result.Code.Should().Be(IntegrationErrors.NoActiveConnection);
        result.Detail.Should().NotContain(SabSpecification.MissingDocument);
    }

    [Fact]
    public void The_adapter_answers_for_its_own_kind()
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        // IntegrationAdapterResolver keys on connection.Kind.ToString() and nothing else.
        harness.Adapter.Kind.Should().Be(IntegrationKind.Sab);
    }

    // ── Health (INT-03) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_health_check_answers_unhealthy_instead_of_throwing()
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        var connection = SabHarness.ConnectionCarrying(SabHarness.ScopedSettings());

        var health = await harness.Adapter.CheckHealthAsync(connection, default);

        // Called from the activation screen and from the `integration` health check. A throwing
        // adapter would turn a known supplier dependency into a 500 that reads like an outage of
        // ours.
        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.AdapterSpecificationPending);
        health.Detail.Should().Contain(SabSpecification.PlanReference);

        // No latency, because nothing was called. Reporting zero would put this row in the
        // activation screen's latency column next to systems that actually answered.
        health.Latency.Should().BeNull();
        health.CheckedAt.Should().NotBe(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task The_health_check_is_unhealthy_however_the_connection_is_configured()
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        var cases = new[]
        {
            SabHarness.ConnectionCarrying(SabHarness.ScopedSettings()),
            SabHarness.ConnectionCarrying(SabHarness.UnscopedSettings()),
            SabHarness.ConnectionCarrying(new TemenosSettings { BaseUrl = "https://example.invalid" }),
        };

        foreach (var connection in cases)
        {
            var health = await harness.Adapter.CheckHealthAsync(connection, default);

            // The structural lock, stated as an invariant rather than as one case: whatever is
            // configured, this check cannot pass. Everything downstream rests on it — see the next
            // test for what an activatable SAB connection would cost.
            health.IsHealthy.Should().BeFalse();
            health.Latency.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_failed_health_check_is_what_keeps_the_connection_inactive()
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        var connection = SabHarness.ConnectionCarrying(SabHarness.ScopedSettings());

        var health = await harness.Adapter.CheckHealthAsync(connection, default);
        connection.RecordHealth(health, TimeProvider.System);

        var activation = connection.Activate(Guid.NewGuid(), TimeProvider.System);

        // The fail-closed outcome, and the SAB-specific stakes are sharper than Perfect Vision's.
        // An active connection here would (1) have the dispatcher reach ports with no mapping
        // behind them, and (2) occupy the tenant's SINGLE active core-banking slot — the partial
        // unique index ux_integration_connection_active_core_banking — so an unmappable SAB row
        // could displace the connection that actually works. Everything the dispatcher queues goes
        // through RequireCoreBankingAsync, which resolves by family AND IsActive, so this one
        // refusal is what makes the whole queueing path unreachable.
        activation.IsSuccess.Should().BeFalse();
        activation.Error.Should().Be(IntegrationErrors.ConnectionNotHealthy);
        connection.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task The_health_check_tells_an_unscoped_connection_apart_from_a_blocked_one()
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        var unscoped = SabHarness.ConnectionCarrying(SabHarness.UnscopedSettings());

        var health = await harness.Adapter.CheckHealthAsync(unscoped, default);

        // Both are unhealthy; only one is anybody's fault here. An administrator looking at an
        // activation screen must see "your connection names no entity" and not a sentence about a
        // document they cannot obtain.
        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
        health.Detail.Should().Contain("settings.entity");
        health.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);
    }

    [Fact]
    public async Task The_health_check_tells_a_missing_api_key_apart_from_a_blocked_one()
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings(), credentialStored: false);

        var connection = SabHarness.ConnectionCarrying(SabHarness.ScopedSettings());

        var health = await harness.Adapter.CheckHealthAsync(connection, default);

        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.CredentialMissing);
        health.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);
    }

    [Fact]
    public async Task Settings_of_the_wrong_shape_are_reported_as_a_configuration_fault()
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        // A row whose settings are not SAB's. Defence in depth — the aggregate refuses the
        // mismatch on write — but the two faults send an administrator to different screens, and
        // collapsing them would send someone to procurement over a mistake they can fix
        // themselves.
        var foreign = SabHarness.ConnectionCarrying(
            new TemenosSettings { BaseUrl = "https://example.invalid" });

        var health = await harness.Adapter.CheckHealthAsync(foreign, default);

        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
        health.Detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);
    }

    [Fact]
    public async Task The_health_check_refuses_a_null_connection_rather_than_answering_about_nothing()
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        var act = async () => await harness.Adapter.CheckHealthAsync(null!, default);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── What the adapter deliberately does and does not claim ───────────────────────────────

    [Fact]
    public void The_tier_read_is_not_claimed_while_the_query_ports_are()
    {
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        // ICbsKycLevelPort's own contract says an implementer MUST declare ReadKycLevel, and
        // whether Open SAB exposes the tier as a readable field is question 3 — so claiming it
        // would have INT-21 compute a compliance divergence from a figure that cannot be obtained.
        harness.Adapter.Should().NotBeAssignableTo<ICbsKycLevelPort>();

        // The query ports ARE claimed, unlike in the Perfect Vision adapter, and the difference is
        // the point: a batch-file CBS answers no query at all, so silence there says "never"; Open
        // SAB is an HTTP API in front of a full ledger, so the same silence here would say "never"
        // about something the catalogue is likely to offer.
        harness.Adapter.Should().BeAssignableTo<ICbsCustomerPort>();
        harness.Adapter.Should().BeAssignableTo<ICbsAccountPort>();
        harness.Adapter.Should().BeAssignableTo<ICbsTransactionPort>();
        harness.Adapter.Should().BeAssignableTo<ICbsLoanPort>();
    }

    [Fact]
    public void The_four_questions_for_SBS_are_carried_in_the_code()
    {
        // Pinned because the list is the deliverable of a blocked chantier: it is what a
        // procurement conversation needs, and a header comment that drifts from the class it
        // documents is a header comment nobody forwards.
        SabSpecification.OpenQuestions.Should().HaveCount(4);

        // Ordered by what unblocks what — the catalogue, then how the key and the entity travel,
        // then which reads exist, then a test environment. The order is part of the content: asked
        // in any other sequence, the answers arrive in a sequence that unblocks nothing.
        SabSpecification.OpenQuestions[0].Should().Contain("catalogue");
        SabSpecification.OpenQuestions[1].Should().Contain("Entity");
        SabSpecification.OpenQuestions[2].Should().Contain("reads");
        SabSpecification.OpenQuestions[3].Should().Contain("TWO entities");
    }

    [Fact]
    public void No_refusal_detail_ever_names_a_path_a_header_or_a_field()
    {
        // The tripwire for the one mistake this whole chantier exists to avoid. An invented
        // identifier in a message is how an invented identifier reaches a mapper: somebody reads
        // the error, takes the name for documentation, and the guess becomes the contract.
        var messages = PortMethodNames()
            .Select(SabSpecification.RefusalDetail)
            .Append(SabSpecification.HealthDetail())
            .Append(SabEntityScope.MissingDetail())
            .ToList();

        foreach (var message in messages)
        {
            message.Should().NotContainAny(
                "http://", "https://", "GET ", "POST ", "PUT ", "/v1", "Bearer", "X-", ".json");
        }
    }

    // ── Invocation plumbing ─────────────────────────────────────────────────────────────────

    private static bool IsOutOfScope(string methodName)
        => methodName is nameof(ICbsAccountPort.DebitAccountAsync)
            or nameof(ICbsAccountPort.ReverseDebitAsync);

    private static IEnumerable<string> PortMethodNames()
        => new[]
            {
                typeof(ICbsCustomerPort), typeof(ICbsAccountPort),
                typeof(ICbsTransactionPort), typeof(ICbsLoanPort),
            }
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
        if (type == typeof(IdempotencyKey)) return new IdempotencyKey("sab-test-key");
        if (type == typeof(ExternalId)) return new ExternalId("SAB-TEST");
        if (type == typeof(KycLevel)) return KycLevel.Simplified;
        if (type == typeof(decimal)) return 0m;
        if (type == typeof(string)) return "unused";

        // Reference payloads: null is safe precisely because the adapter refuses before reading
        // anything, and a test that had to build one would be asserting about the payload instead.
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
