namespace Sankore.Modules.Integration.Tests.Adapters.Ringover;

using System.Reflection;
using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Ringover;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// TEL-B-04 — the port is in place, every operation that needs the provider refuses while naming
/// the one thing missing for it, and the three refusals that are the TENANT's own are told apart
/// from the one that is a procurement conversation.
///
/// <para>
/// What these tests defend is not a behaviour, it is a decision: that an adapter with no test
/// account must say so rather than guess. M02's biometry client is the counter-example the
/// repository already paid for. A guessed telephony call fails worse, and more quietly: a guessed
/// STATUS succeeds — the journal accepts it, the call row changes, and nothing reports it — so a
/// conversation is recorded as missed and somebody is rung to apologise for a call that already
/// happened, or a missed call is recorded as completed and stops a lead's first-contact clock for
/// a conversation nobody had.
/// </para>
/// </summary>
public sealed class RingoverAdapterTests
{
    /// <summary>
    /// Every method of the port, discovered rather than listed, minus the two that answer for a
    /// different reason and are asserted on their own.
    ///
    /// <para>
    /// Reflection on purpose. A hand-written list of eight calls goes stale the day the port grows
    /// a ninth — exactly the day it matters, because a new port method returning a default
    /// <c>IntegrationResult</c> would be the only silent success in an adapter whose contract is
    /// to refuse.
    /// </para>
    /// </summary>
    public static TheoryData<string> RefusingMethods()
    {
        var data = new TheoryData<string>();

        foreach (var name in PortMethodNames().Where(n => !AnswersDifferently(n)))
            data.Add(name);

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusingMethods))]
    public async Task Every_provider_operation_refuses_and_names_the_missing_account(string methodName)
    {
        using var harness = RingoverHarness.With(RingoverHarness.ConfiguredSettings());

        var result = await InvokeAsync(harness.Adapter, methodName);

        result.IsFailure.Should().BeTrue($"{methodName} cannot be served without a test account");
        result.Code.Should().Be(IntegrationErrors.AdapterSpecificationPending);

        // Technical and never Transient: the family decides what the platform does next, and
        // retrying with backoff cannot obtain an account. Technical parks the work and alerts the
        // administrator, which is the action that can actually move this forward.
        result.Family.Should().Be(ErrorFamily.Technical);
        result.IsRetryable.Should().BeFalse();

        // The operator-facing half. An error that said "not implemented" would send someone to
        // raise a defect and someone else to look for the half-written method; this one says what
        // is missing, from whom, and where that is recorded.
        result.Detail.Should().NotBeNullOrWhiteSpace();
        result.Detail.Should().Contain(RingoverSpecification.MissingPrerequisites);
        result.Detail.Should().Contain(RingoverSpecification.PlanReference);
        result.Detail.Should().Contain("Ringover", "the reader has to know who owns the blocker");
        result.Detail.Should().NotContain("not implemented");
    }

    [Fact]
    public async Task Deleting_a_recording_answers_CAPABILITY_NOT_SUPPORTED_and_not_the_pending_account()
    {
        using var harness = RingoverHarness.With(RingoverHarness.ConfiguredSettings());

        var result = await harness.Adapter.DeleteRecordingAsync(
            new ExternalId("RINGOVER-TEST"), CancellationToken.None);

        // The distinction is the one thing this case exists for. "Pending account" promises the
        // provider's copy will one day be destroyed, which is open question 6 and not a
        // certainty; CapabilityNotSupported says the retention reaches SANKORE's copy only, which
        // is a sentence somebody can forward to a regulator.
        result.Code.Should().Be(IntegrationErrors.CapabilityNotSupported);
        result.Detail.Should().NotContain(RingoverSpecification.MissingPrerequisites);
        result.Detail.Should().Contain("purge");
    }

    [Fact]
    public async Task A_connection_with_no_API_host_is_the_TENANTS_own_refusal()
    {
        using var harness = RingoverHarness.With(RingoverHarness.HostlessSettings());

        var result = await harness.Adapter.ListUsersAsync(CancellationToken.None);

        // Not the pending account: this one is fixed on the tenant's own settings screen in a
        // minute, and answering "we are waiting for Ringover" would leave it broken for a reason
        // nobody owns.
        result.Code.Should().Be(IntegrationErrors.SettingsInvalid);
        result.Detail.Should().NotContain(RingoverSpecification.MissingPrerequisites);
    }

    [Fact]
    public async Task A_missing_API_key_and_a_missing_webhook_token_are_told_apart()
    {
        using (var noKey = RingoverHarness.With(
            RingoverHarness.ConfiguredSettings(), apiKeyStored: false))
        {
            var result = await noKey.Adapter.ListUsersAsync(CancellationToken.None);

            result.Code.Should().Be(IntegrationErrors.CredentialMissing);
            result.Detail.Should().Contain("API key");
        }

        using var noToken = RingoverHarness.With(
            RingoverHarness.ConfiguredSettings(), webhookSecretStored: false);

        var second = await noToken.Adapter.ListUsersAsync(CancellationToken.None);

        // They break different halves: without the key nothing can be asked of the provider,
        // while without the token the provider's events are refused at the door — so an agent
        // would place calls and see none of them journalled, which looks like a different fault.
        second.Code.Should().Be(IntegrationErrors.CredentialMissing);
        second.Detail.Should().Contain("webhook token");
    }

    [Fact]
    public async Task A_tenant_with_no_Ringover_connection_is_answered_by_name_and_not_by_throwing()
    {
        using var harness = RingoverHarness.WithNoConnection();

        // A port method carries no connection, by design, so the adapter has to find the row
        // itself — and an exception out of a Hangfire job is a stack trace, not an answer.
        var result = await harness.Adapter.GetCallAsync(
            new ExternalId("RINGOVER-TEST"), CancellationToken.None);

        result.Code.Should().Be(IntegrationErrors.ConnectionNotFound);
    }

    [Fact]
    public async Task The_vault_is_asked_for_a_HINT_and_never_for_a_value()
    {
        using var harness = RingoverHarness.With(RingoverHarness.ConfiguredSettings());

        await harness.Adapter.ListUsersAsync(CancellationToken.None);

        // Nothing can be done with the key until the vocabulary is known, so a value read here
        // would be a secret decrypted into the memory of a process that has no use for it —
        // gratuitous exposure, on a path a logger or an exception filter could later widen.
        await harness.Secrets.DidNotReceive().GetValueAsync(
            Arg.Any<SecretKey>(), Arg.Any<CancellationToken>());

        await harness.Secrets.Received().GetHintAsync(
            Arg.Is<SecretKey>(k =>
                k.Scope == RingoverSecretNames.Scope
                && k.TenantId == RingoverHarness.TenantId
                && k.EntityId == RingoverHarness.ConnectionId
                && k.Name == RingoverSecretNames.Credential),
            Arg.Any<CancellationToken>());

        await harness.Secrets.Received().GetHintAsync(
            Arg.Is<SecretKey>(k =>
                k.EntityId == RingoverHarness.ConnectionId
                && k.Name == RingoverSecretNames.WebhookSecret),
            Arg.Any<CancellationToken>());
    }

    // ── Health ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_health_check_always_answers_unhealthy_so_the_connection_cannot_be_activated()
    {
        using var harness = RingoverHarness.With(RingoverHarness.ConfiguredSettings());

        var connection = RingoverHarness.ConnectionCarrying(RingoverHarness.ConfiguredSettings());

        var health = await harness.Adapter.CheckHealthAsync(connection, CancellationToken.None);

        // This is what makes registering the adapter safe. ActivateConnectionHandler requires a
        // passing health check, so a Ringover connection can be created and configured and will
        // stay inactive — which is the honest state, and the reason the composition test could
        // flip to "registered" without letting anybody switch on telephony that does not work.
        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.AdapterSpecificationPending);
        health.Detail.Should().Contain(RingoverSpecification.MissingPrerequisites);
    }

    [Fact]
    public async Task The_health_check_names_the_tenants_own_faults_before_the_account()
    {
        using var harness = RingoverHarness.With(RingoverHarness.ConfiguredSettings());

        var hostless = await harness.Adapter.CheckHealthAsync(
            RingoverHarness.ConnectionCarrying(RingoverHarness.HostlessSettings()),
            CancellationToken.None);

        hostless.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
        hostless.Detail.Should().NotContain(RingoverSpecification.MissingPrerequisites);

        // And a row carrying another kind's settings entirely, which the aggregate would refuse to
        // create — so it is built by hand and handed straight in.
        var foreign = await harness.Adapter.CheckHealthAsync(
            RingoverHarness.ConnectionCarrying(new FakeSettings()), CancellationToken.None);

        foreign.Detail.Should().Contain(IntegrationErrors.SettingsInvalid);
    }

    // ── The webhook parser: the one member that WORKS ─────────────────────────

    [Fact]
    public void The_webhook_parser_makes_an_identical_redelivery_the_SAME_event()
    {
        using var harness = RingoverHarness.With(RingoverHarness.ConfiguredSettings());

        var body = """{"anything":"at all"}""";

        var first = harness.Adapter.ParseWebhook(Request(body));
        var second = harness.Adapter.ParseWebhook(Request(body));

        // The whole purpose of a dedup key, and the reason it is a hash of the body rather than a
        // fresh identifier: tel_webhook_event makes a replay a no-op, and a new key per delivery
        // would make every redelivery a new event — which is the duplicate it exists to stop.
        first.DedupKey.Should().Be(second.DedupKey);
        first.DedupKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Two_different_bodies_are_two_different_events()
    {
        using var harness = RingoverHarness.With(RingoverHarness.ConfiguredSettings());

        var first = harness.Adapter.ParseWebhook(Request("""{"a":1}"""));
        var second = harness.Adapter.ParseWebhook(Request("""{"a":2}"""));

        first.DedupKey.Should().NotBe(second.DedupKey);
    }

    [Fact]
    public void The_webhook_parser_guesses_NOTHING_about_the_call()
    {
        using var harness = RingoverHarness.With(RingoverHarness.ConfiguredSettings());

        // A body that LOOKS like it carries everything. A parser that guessed field names would
        // find three of them here and be wrong about all three, because this vocabulary is
        // invented — by this test, deliberately, which is the point.
        var parsed = harness.Adapter.ParseWebhook(Request(
            """{"call_id":"abc","status":"ANSWERED","timestamp":"2026-03-11T09:00:00Z"}"""));

        parsed.CallId.Should().BeNull();
        parsed.Status.Should().BeNull();
        parsed.OccurredAt.Should().BeNull();

        // And the event type says what it is rather than naming a provider event nobody has seen.
        parsed.EventType.Should().Be("ringover.unparsed");
        parsed.DedupKey.Should().StartWith("ringover-unparsed:");
    }

    [Fact]
    public void An_empty_body_still_parses_rather_than_throwing()
    {
        using var harness = RingoverHarness.With(RingoverHarness.ConfiguredSettings());

        // It is called on an anonymous request. Throwing would turn a malformed POST into a 500
        // and a log entry an attacker controls the volume of.
        var parsed = harness.Adapter.ParseWebhook(Request(string.Empty));

        parsed.DedupKey.Should().NotBeNullOrWhiteSpace();
    }

    // ── What the assembly must never contain ──────────────────────────────────

    [Fact]
    public void No_invented_provider_identifier_appears_in_the_specification_strings()
    {
        var text = string.Join(
            '\n',
            [
                RingoverSpecification.MissingPrerequisites,
                RingoverSpecification.HealthDetail(),
                RingoverSpecification.RefusalDetail(RingoverOperations.GetCall),
                .. RingoverSpecification.OpenQuestions,
            ]);

        // An invented identifier in a message is how an invented identifier reaches a translator:
        // somebody reads the error, takes the name for documentation, and the guess becomes the
        // contract. These are the shapes one would take — a path, a URL, a header, a token.
        text.Should().NotContain("/v1");
        text.Should().NotContain("http://");
        text.Should().NotContain("https://");
        text.Should().NotContain("Bearer ");
        text.Should().NotContain("X-");
    }

    [Fact]
    public void The_six_open_questions_are_present_numbered_and_name_their_fallbacks()
    {
        var questions = RingoverSpecification.OpenQuestions;

        questions.Should().HaveCount(6);

        for (var i = 0; i < questions.Count; i++)
            questions[i].Should().StartWith($"{i + 1}.");

        // The three with a fallback say so, because the opposite reading — "six open points, so
        // six things do not work" — is wrong and would have somebody rebuild what exists.
        questions.Count(q => q.Contains("FALLBACK ALREADY IMPLEMENTED")).Should().Be(3);

        // And the first one is the test account, because it is the only one that cannot be
        // answered by reading anything.
        questions[0].Should().Contain("test account");
    }

    [Fact]
    public void The_three_fallbacks_each_name_where_they_are_already_implemented()
    {
        RingoverFallbacks.All.Should().HaveCount(3);

        // Each one points at the production code that already does it, so a future change that
        // quietly removes a fallback has somewhere to fail.
        RingoverFallbacks.WebhookAuthenticity.Should().Contain("SyncWebhookVerifier");
        RingoverFallbacks.ClickToCallCorrelation.Should().Contain("TelCallJournalWriter");
        RingoverFallbacks.RecordingDeletion.Should().Contain("PurgeTelephonyRecordingsJob");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static TelephonyWebhookRequest Request(string body)
        => new(body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The two members that do not answer <c>AdapterSpecificationPending</c>:
    /// <c>DeleteRecordingAsync</c> answers <c>CapabilityNotSupported</c> because the capability is
    /// not declared, and <c>ParseWebhook</c> is implemented and returns no result at all.
    /// </summary>
    private static bool AnswersDifferently(string name)
        => name is nameof(ITelephonyPort.DeleteRecordingAsync)
                or nameof(ITelephonyPort.ParseWebhook);

    private static IEnumerable<string> PortMethodNames()
        => typeof(ITelephonyPort).GetMethods()
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

    /// <summary>
    /// Calls one port method by name with harmless arguments and hands back the result as the
    /// non-generic base.
    ///
    /// <para>
    /// The arguments are deliberately uninteresting: this adapter reads none of them, and a
    /// fixture built to look realistic would suggest the refusal depended on them.
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
        if (type == typeof(ExternalId)) return new ExternalId("RINGOVER-TEST");
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.UnixEpoch;
        if (type == typeof(string)) return "unused";

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
