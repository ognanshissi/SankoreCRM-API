namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Missed;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Closing a missed call without calling back (TEL-F-09) — the second way out of the follow-up
/// list, and the one that does NOT claim a conversation happened.
///
/// <para>
/// The departure from the list is asserted through <c>ListMissedCallsHandler</c> rather than by
/// reading the column, because that is the behaviour an agent paid for: a dismissal that wrote a
/// timestamp nothing filtered on would be a button that does nothing, and agents would go back to
/// inventing an outcome code for "nothing" — which the institution would then count in its
/// contact figures.
/// </para>
/// </summary>
public sealed class DismissMissedCallTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);

    private const string Reason = "Le client a rappelé l'agence et a été servi au guichet.";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public DismissMissedCallTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    // ── What leaves the follow-up list ────────────────────────────────────────

    [Fact]
    public async Task A_dismissed_missed_call_leaves_the_follow_up_list()
    {
        var call = await SeedAsync(TelCallStatus.Missed, crmUserId: _me);

        (await ListAsync()).Should().HaveCount(1);

        (await DismissAsync(call)).IsSuccess.Should().BeTrue();

        // Asserted through the list and not through the column: the point of the feature is that
        // the entry stops coming back, and a timestamp nobody reads is not that.
        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_VOICEMAIL_call_can_be_dismissed_too()
    {
        var call = await SeedAsync(TelCallStatus.Voicemail, crmUserId: _me);

        (await DismissAsync(call)).IsSuccess.Should().BeTrue();

        // Somebody left a message, which the follow-up list treats as the clearest possible
        // request for a call back — so it is in the list, and it has to be closable from there.
        // Refusing it would leave the one entry an agent most wants to answer with "already
        // handled at the counter" stuck in their queue for ever.
        (await ListAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(TelCallStatus.Completed)]
    [InlineData(TelCallStatus.Failed)]
    [InlineData(TelCallStatus.Ringing)]
    [InlineData(TelCallStatus.InProgress)]
    public async Task A_call_that_was_never_missed_is_refused(TelCallStatus status)
    {
        var call = await SeedAsync(status, crmUserId: _me);

        var result = await DismissAsync(call);

        // A conversation that took place has nothing to dismiss, and allowing it would be a way
        // to clear a call out of a queue without qualifying it — the paperwork avoided rather
        // than done. Failed and Ringing are refused for the plainer reason that neither is in the
        // follow-up list to begin with.
        result.Error.Should().Be(TelephonyErrors.CallNotMissed);

        (await ReloadAsync(call)).DismissedAt.Should().BeNull();
    }

    [Fact]
    public async Task Dismissing_twice_is_a_SUCCESS_and_keeps_the_FIRST_instant_and_author()
    {
        var call = await SeedAsync(TelCallStatus.Missed, crmUserId: null);

        var first = Now.AddMinutes(-30);

        (await DismissAsync(call, actor: _me, at: first)).IsSuccess.Should().BeTrue();

        var second = await DismissAsync(call, actor: _colleague, at: Now);

        // A double-clicked button, or two agents working the same branch queue, must not read as
        // the rule firing: a conflict here would send a screen an error about something that is
        // already exactly as the agent wanted it.
        second.IsSuccess.Should().BeTrue();

        var row = await ReloadAsync(call);

        // And the FIRST closure stands, because it is the one the audit row explains. Letting the
        // second overwrite it would re-date a decision to whenever somebody next clicked, and
        // re-attribute it to whoever that was.
        row.DismissedAt.Should().Be(first);
        row.DismissedBy.Should().Be(_me);
    }

    // ── Who may close one ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_colleagues_call_answers_NOT_FOUND_and_not_a_refusal()
    {
        var call = await SeedAsync(TelCallStatus.Missed, crmUserId: _colleague);

        var result = await DismissAsync(call);

        // Not a 403, which is the whole point: a 403 would confirm that this call exists and that
        // somebody tried to reach a colleague — a fact about a person, handed out by the error
        // code. The call is also not theirs to close: showing it to two agents is how one person
        // gets called back twice.
        result.Error.Should().Be(TelephonyErrors.CallNotFound);

        (await ReloadAsync(call)).DismissedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_call_attributed_to_NOBODY_may_be_closed_by_anybody()
    {
        var call = await SeedAsync(TelCallStatus.Missed, crmUserId: null);

        // The agency's "to attribute" queue is worked by the whole branch — which is why the
        // missed list shows it to everyone — so it has to be closable by them too. Requiring an
        // owner would leave the one pile nobody owns as the one pile nobody can clear.
        (await DismissAsync(call, actor: _colleague)).IsSuccess.Should().BeTrue();

        (await ReloadAsync(call)).DismissedBy.Should().Be(_colleague);
    }

    [Fact]
    public async Task Another_tenants_call_answers_NOT_FOUND()
    {
        var call = await SeedAsync(
            TelCallStatus.Missed, crmUserId: _me, tenantId: Guid.NewGuid());

        // Seeded for MY user id, so the global query filter is the only thing answering here —
        // and the existence of a call is itself information about who an institution spoke to.
        (await DismissAsync(call)).Error.Should().Be(TelephonyErrors.CallNotFound);
    }

    [Fact]
    public async Task An_unknown_call_answers_NOT_FOUND()
        => (await DismissAsync(Guid.NewGuid())).Error.Should().Be(TelephonyErrors.CallNotFound);

    // ── The reason ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_closure_with_no_reason_is_refused(string? reason)
    {
        var result = new DismissMissedCallValidator()
            .Validate(new DismissMissedCallCommand(Guid.NewGuid(), reason));

        // The whole value of closing an entry without calling back is that somebody said why: an
        // entry closed with nothing is indistinguishable from one quietly lost, and a reviewer
        // asking months later has no way to tell them apart. Whitespace counts as nothing, or the
        // rule is one space away from being optional.
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be("Reason");
    }

    [Fact]
    public void A_reason_longer_than_five_hundred_characters_is_refused()
    {
        var validator = new DismissMissedCallValidator();

        validator.Validate(new DismissMissedCallCommand(Guid.NewGuid(), new string('x', 500)))
            .IsValid.Should().BeTrue();

        // Bounded because it lands in an audit payload, not in a column sized for an essay — and
        // a free-text field with no ceiling is how a 50 kB paste becomes a row nothing can render.
        validator.Validate(new DismissMissedCallCommand(Guid.NewGuid(), new string('x', 501)))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_ordinary_reason_is_accepted()
        => new DismissMissedCallValidator()
            .Validate(new DismissMissedCallCommand(Guid.NewGuid(), Reason))
            .IsValid.Should().BeTrue();

    [Fact]
    public async Task The_reason_is_nowhere_on_the_call()
    {
        var call = await SeedAsync(TelCallStatus.Missed, crmUserId: _me);

        await DismissAsync(call);

        // Structural, because it is a deliberate choice rather than an omission: a column would
        // hold the LAST reason only, while what a later reader needs is the sequence — an entry
        // closed, reopened by a call back and closed again is a different story from one closed
        // once. The command is an ICommand so the payload lands in the audit row, where reading
        // it is itself gated.
        typeof(TelCall).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Reason", StringComparison.OrdinalIgnoreCase));

        var row = await ReloadAsync(call);

        row.DismissedAt.Should().NotBeNull();
        row.DismissedBy.Should().Be(_me);
    }

    // ── TelCall.Dismiss, on its own ───────────────────────────────────────────

    [Fact]
    public void Dismissing_does_not_touch_the_wrap_up()
    {
        var call = NewCall(TelCallStatus.Missed);

        call.Dismiss(_me, Now).IsSuccess.Should().BeTrue();

        // The two are different answers and both may be true of one call: a dismissal says
        // nobody needs calling back, a wrap-up describes a conversation, creates a lead activity
        // and may stop an SLA clock. Writing a WrappedUpAt here would have the journal claim a
        // conversation that never happened — and stop a prospect's first-contact clock with it.
        call.WrappedUpAt.Should().BeNull();
        call.OutcomeCode.Should().BeNull();
        call.Status.Should().Be(TelCallStatus.Missed);
    }

    [Fact]
    public void Dismissing_a_call_that_was_already_qualified_leaves_the_qualification_alone()
    {
        var call = NewCall(TelCallStatus.Missed);

        call.WrapUp("NOT_REACHED", noteEnc: null, Now.AddMinutes(-10))
            .IsSuccess.Should().BeTrue();

        call.Dismiss(_me, Now).IsSuccess.Should().BeTrue();

        // The reverse direction of the same rule. An agent who called back, noted the outcome and
        // then closed the entry has done two separate things, and the second must not erase the
        // first — the outcome is what the contact figures are counted from.
        call.OutcomeCode.Should().Be("NOT_REACHED");
        call.WrappedUpAt.Should().Be(Now.AddMinutes(-10));
        call.DismissedAt.Should().Be(Now);
    }

    [Fact]
    public void Anonymising_a_call_KEEPS_its_dismissal()
    {
        var call = NewCall(TelCallStatus.Missed);

        call.WrapUp("NOT_REACHED", Encoding.UTF8.GetBytes("v1:note"), Now.AddMinutes(-10))
            .IsSuccess.Should().BeTrue();

        call.Dismiss(_me, Now).IsSuccess.Should().BeTrue();

        call.Anonymise();

        // The decision not to call somebody back is a decision of the INSTITUTION, not personal
        // data about the person: it is what explains, years later, why an entry in a follow-up
        // list was closed and nobody rang. Blanking it with the numbers would leave a retention
        // sweep rewriting the institution's own record of what it chose to do.
        call.DismissedAt.Should().Be(Now);
        call.DismissedBy.Should().Be(_me);

        // And what anonymisation is actually for is gone.
        call.FromNumberEnc.Should().BeEmpty();
        call.ToNumberEnc.Should().BeEmpty();
        call.NoteEnc.Should().BeNull();

        // The reporting axes stay, or a retention rule becomes a reporting outage.
        call.OutcomeCode.Should().Be("NOT_REACHED");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private TelCall NewCall(TelCallStatus status, Guid? crmUserId = null, Guid? tenantId = null)
    {
        var protector = TelephonyTestHarness.Numbers();
        var from = protector.Protect("+22507111111");
        var to = protector.Protect("+22507000001");
        var at = Now.AddHours(-1);

        var call = TelCall.Open(
            tenantId ?? _tenant, _connection, $"prov-{Guid.NewGuid()}",
            TelCallDirection.Inbound, TelCallStatus.Ringing,
            from.Encrypted, from.BlindIndex, to.Encrypted, to.BlindIndex,
            at, externalUserId: null, crmUserId: crmUserId);

        // Through the transitions, so EndedAt is written by the move the way a provider's webhook
        // writes it — the follow-up list and the wrap-up rule both read it.
        if (status is TelCallStatus.Completed)
            call.TryMoveTo(TelCallStatus.InProgress, at.AddSeconds(10));

        if (status != TelCallStatus.Ringing)
            call.TryMoveTo(status, at.AddSeconds(90));

        return call;
    }

    private async Task<Guid> SeedAsync(
        TelCallStatus status, Guid? crmUserId, Guid? tenantId = null)
    {
        var tenant = tenantId ?? _tenant;

        await using var db = _factory.ContextFor(tenant);

        var call = NewCall(status, crmUserId, tenant);

        db.TelCalls.Add(call);
        await db.SaveChangesAsync(CancellationToken.None);

        return call.Id;
    }

    private async Task<Result> DismissAsync(
        Guid callId, Guid? actor = null, DateTimeOffset? at = null, string? reason = Reason)
    {
        await using var db = _factory.CreateContext();

        return await new DismissMissedCallHandler(
                db,
                TelephonyTestHarness.User(_tenant, actor ?? _me),
                new TelephonyTestHarness.FixedClock(at ?? Now),
                NullLogger<DismissMissedCallHandler>.Instance)
            .Handle(new DismissMissedCallCommand(callId, reason), CancellationToken.None);
    }

    private async Task<TelCall> ReloadAsync(Guid callId)
    {
        await using var db = _factory.CreateContext();

        return await db.TelCalls.FirstAsync(c => c.Id == callId, CancellationToken.None);
    }

    /// <summary>
    /// The agent's follow-up list as the screen reads it — the surface a dismissal exists to
    /// shorten.
    /// </summary>
    private async Task<IReadOnlyList<MissedCallDto>> ListAsync()
    {
        await using var db = _factory.CreateContext();

        var result = await new ListMissedCallsHandler(
                db, TelephonyTestHarness.User(_tenant, _me))
            .Handle(new ListMissedCallsQuery(Now.AddDays(-7)), CancellationToken.None);

        return result.Value;
    }
}
