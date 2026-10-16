namespace Sankore.Modules.Integration.Tests.Contract;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// What every telephony adapter must do, whoever the provider is (TEL-B-21).
///
/// <para>
/// Inherits <see cref="IntegrationEntityPortContractTests{TPort}"/> and not the write variant: no
/// operation here takes an idempotency key, and that absence is a decision rather than a gap. A
/// replayed write to a back office is a duplicate row somebody reconciles; a replayed CALL is a
/// telephone ringing twice in somebody's hand, and no key makes that safe — so the caller does
/// not retry, and the contract has nothing to assert about replays.
/// </para>
///
/// <para>
/// A new provider's whole test obligation is to subclass this and say how to build its port.
/// </para>
/// </summary>
public abstract class TelephonyPortContractTests : IntegrationEntityPortContractTests<ITelephonyPort>
{
    /// <summary>A call the provider knows about.</summary>
    protected abstract ExistingCall KnownCall { get; }

    /// <summary>One of the provider's own users, for placing a call.</summary>
    protected abstract ExternalId KnownUserId { get; }

    protected override async Task<(IntegrationResult Result, Action ReadValue)> AnyValuedCallAsync(
        ITelephonyPort port)
    {
        var result = await port.GetCallAsync(KnownCall.Id, CancellationToken.None);
        return (result, () => _ = result.Value);
    }

    protected override Task<IntegrationResult> CallAgainstAnAbsentEntityAsync(ITelephonyPort port)
        => port.GetCallAsync(AbsentId, CancellationToken.None)
               .ContinueWith(t => (IntegrationResult)t.Result, TaskScheduler.Default);

    [Fact]
    public async Task The_directory_should_answer_users_with_an_id_and_a_usable_flag()
    {
        var result = await CreatePort().ListUsersAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // An id and a usable flag are the two the linking screen cannot work without: one to
        // store, one to tell a leaver's account from a live one. A name and an address are
        // conveniences a provider may withhold.
        result.Value.Should().OnlyContain(u => !string.IsNullOrWhiteSpace(u.Id.Value));
    }

    [Fact]
    public async Task Reading_a_known_call_should_answer_its_direction_and_its_status()
    {
        var result = await CreatePort().GetCallAsync(KnownCall.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(KnownCall.Id);
        result.Value.Direction.Should().Be(KnownCall.Direction);
        result.Value.Status.Should().Be(KnownCall.Status);
    }

    [Fact]
    public async Task A_completed_call_should_carry_the_instants_its_durations_describe()
    {
        var result = await CreatePort().GetCallAsync(KnownCall.Id, CancellationToken.None);

        var call = result.Value;

        // The journal computes nothing from these — it stores the provider's own figures — but a
        // call that was answered and never ended would make every duration on the screen a
        // negative number, and that is the adapter's translation to get right.
        if (call.AnsweredAt is not null)
            call.AnsweredAt.Should().BeOnOrAfter(call.StartedAt);

        if (call.EndedAt is not null)
            call.EndedAt.Should().BeOnOrAfter(call.StartedAt);

        call.RingSeconds.Should().Match(s => s == null || s >= 0);
        call.TalkSeconds.Should().Match(s => s == null || s >= 0);
    }

    [Fact]
    public async Task Listing_calls_should_answer_only_calls_at_or_after_the_instant_asked_for()
    {
        var sut = CreatePort();

        var result = await sut.ListCallsAsync(KnownCall.StartedAt, cursor: null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Calls.Should().OnlyContain(c => c.StartedAt >= KnownCall.StartedAt);
    }

    [Fact]
    public async Task Listing_calls_far_in_the_future_should_be_empty_and_not_a_failure()
    {
        // The catch-up sweep asks from its cursor on every run, and most runs have nothing to
        // fetch. A provider that answered an error to "nothing since then" would make the
        // ordinary case look like an incident every quarter of an hour.
        var result = await CreatePort().ListCallsAsync(
            KnownCall.StartedAt.AddYears(50), cursor: null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Placing_a_call_should_answer_a_reference_the_journal_can_correlate_on()
    {
        var result = await CreatePort().StartCallbackAsync(
            KnownUserId, targetNumber: "+22507222222", callerNumber: null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().NotBeNullOrWhiteSpace(
            "without a reference the request can only be correlated by user, number and a "
            + "two-minute window, which is the fallback and not the contract");
    }

    [Fact]
    public async Task Downloading_a_recording_that_does_not_exist_should_be_functional_not_technical()
    {
        // A call with no recording is an ordinary call — the tenant may simply not record. A
        // technical failure here would have the dispatcher retry something that will never exist.
        var result = await CreatePort().DownloadRecordingAsync(AbsentId, CancellationToken.None);

        ShouldBeAbsentEntity(result);
    }

    [Fact]
    public void Parsing_a_webhook_should_produce_a_deduplication_key_whatever_the_body_says()
    {
        // The key is the only member with no fallback: without it the event cannot be made
        // idempotent, and a provider redelivering would be written twice.
        var parsed = CreatePort().ParseWebhook(
            new TelephonyWebhookRequest("ringing:" + KnownCall.Id.Value, Headers: new Dictionary<string, string>()));

        parsed.DedupKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Parsing_a_webhook_this_adapter_does_not_understand_should_not_throw()
    {
        // It runs on an anonymous request, before anything has been verified. A parser that threw
        // would turn an unverified body into a 500 — and a way to make the endpoint spend work.
        var act = () => CreatePort().ParseWebhook(
            new TelephonyWebhookRequest("}{ not a payload", Headers: new Dictionary<string, string>()));

        act.Should().NotThrow();
    }

    /// <summary>A call the adapter under test can resolve, and what it should say about it.</summary>
    public sealed record ExistingCall(
        ExternalId Id,
        TelCallDirection Direction,
        TelCallStatus Status,
        DateTimeOffset StartedAt);
}
