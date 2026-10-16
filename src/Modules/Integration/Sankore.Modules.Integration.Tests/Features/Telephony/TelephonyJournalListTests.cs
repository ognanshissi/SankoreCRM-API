namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.Features.Telephony.Outcomes;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The call journal (TEL-B-08, TEL-F-07, TEL-F-08): who may see which rows, what every filter
/// means, and the three work queues.
///
/// <para>
/// The perimeter cases come first and are written one rule per test rather than one test per
/// screenful, because this is the first telephony read whose scope is not fixed by construction:
/// the missed list is "mine or nobody's" and the record timeline is one record, while this one
/// could hand any holder of <c>Telephony.Call.View</c> the complete telephone activity of every
/// branch. A test that asserted four rows at once would still pass with three of the four rules
/// wrong.
/// </para>
/// </summary>
public sealed class TelephonyJournalListTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly Guid _myAgency = Guid.NewGuid();
    private readonly Guid _otherAgency = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public TelephonyJournalListTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();

        TelCallOutcomeSeeder.SeedTenantAsync(db, _tenant, CancellationToken.None)
            .GetAwaiter().GetResult();
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    // ── The perimeter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_restricted_caller_sees_their_OWN_call_whatever_agency_it_sits_in()
    {
        await SeedAsync(crmUserId: _me, agencyId: _otherAgency);

        // My own call is mine wherever the agency column points — a head-office user placing a
        // call for a branch customer is ordinary, and losing it from my own journal would make
        // the one list an agent trusts about themselves incomplete.
        (await ReadAsync(scope: [_myAgency])).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_restricted_caller_sees_a_call_attributed_to_NOBODY()
    {
        await SeedAsync(crmUserId: null, agencyId: null);

        // Deliberate and not a hole: a call from a provider account no link resolves is the call
        // most likely to be forgotten, and hiding it until somebody repairs a settings row loses
        // exactly the history that repair would explain. The missed list already shows it to
        // everyone for the same reason.
        (await ReadAsync(scope: [_myAgency])).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_restricted_caller_sees_a_colleagues_call_in_an_agency_they_cover()
    {
        await SeedAsync(crmUserId: _colleague, agencyId: _myAgency);

        // The supervision case — the whole reason tel_call carries an agency. Without it a branch
        // manager could read nothing but their own handset.
        (await ReadAsync(scope: [_myAgency])).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_restricted_caller_does_NOT_see_a_colleagues_call_OUTSIDE_their_scope()
    {
        await SeedAsync(crmUserId: _colleague, agencyId: _otherAgency);

        // The defect this endpoint exists not to have. One permission would otherwise publish who
        // every branch spoke to, when, for how long and with what outcome.
        (await ReadAsync(scope: [_myAgency])).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task An_UNRESTRICTED_caller_sees_the_tenant_whole()
    {
        await SeedAsync(crmUserId: _colleague, agencyId: _otherAgency);
        await SeedAsync(crmUserId: _colleague, agencyId: null);
        await SeedAsync(crmUserId: null, agencyId: _otherAgency);

        // IAgencyScopeProvider signals "unrestricted" with a NULL set, never "no access". Reading
        // it the other way round would lock out the one account whose job is to see everything —
        // and it would do so silently, with an empty list that looks like a quiet tenant.
        (await ReadAsync(scope: null)).Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_caller_who_supervises_NOTHING_still_sees_their_own_and_nobodys()
    {
        await SeedAsync(crmUserId: _me, agencyId: _myAgency);
        await SeedAsync(crmUserId: null, agencyId: _myAgency);
        await SeedAsync(crmUserId: _colleague, agencyId: _myAgency);

        // An EMPTY scope is the opposite of a null one: an agent who supervises no branch. The
        // two halves the helper keeps out of the agency question — mine, and nobody's — are what
        // is left, and an ordinary agent must keep seeing those.
        (await ReadAsync(scope: [])).Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_colleagues_call_with_a_NULL_agency_is_invisible_to_a_restricted_caller()
    {
        await SeedAsync(crmUserId: _colleague, agencyId: null);

        // Asserted as the code behaves, and it is the stated rule rather than an oversight: a
        // null agency is inside no RESTRICTED perimeter — never inside all of them. The cost is
        // real and worth naming: a colleague's call that M12 could not place sits in nobody's
        // journal but a super-user's until StampAgency repairs it on a later reading.
        (await ReadAsync(scope: [_myAgency])).Items.Should().BeEmpty();

        // And it is not lost to the institution.
        (await ReadAsync(scope: null)).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Another_tenants_calls_are_invisible()
    {
        var otherTenant = Guid.NewGuid();

        await SeedAsync(crmUserId: _me, agencyId: _myAgency, tenantId: otherTenant);

        // The handler writes no tenant predicate of its own — the global query filter is the one
        // guarantee — so this is the test that says the filter is actually reached. Note it is
        // seeded as MY user id and MY agency: the only thing keeping it out is the tenant.
        (await ReadAsync(scope: null)).Items.Should().BeEmpty();

        await using var other = _factory.ContextFor(otherTenant);

        // And the row is really there, so the emptiness above is isolation and not a broken seed.
        (await other.TelCalls.CountAsync(CancellationToken.None)).Should().Be(1);
    }

    // ── Every filter, one at a time ───────────────────────────────────────────

    [Fact]
    public async Task From_and_To_bound_the_period_on_the_start_of_the_call()
    {
        await SeedAsync(crmUserId: _me, startedAt: Now.AddDays(-10));
        await SeedAsync(crmUserId: _me, startedAt: Now.AddDays(-1));

        (await ReadAsync(new ListTelephonyCallsQuery(From: Now.AddDays(-5))))
            .Items.Should().HaveCount(1);

        (await ReadAsync(new ListTelephonyCallsQuery(To: Now.AddDays(-5))))
            .Items.Should().HaveCount(1);

        // Both bounds are inclusive, which is what a day picker sending midnight-to-midnight
        // needs: an exclusive end would drop every call of the last day in the range.
        (await ReadAsync(new ListTelephonyCallsQuery(
                From: Now.AddDays(-10), To: Now.AddDays(-1))))
            .Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_crmUserId_filter_narrows_to_one_agent()
    {
        await SeedAsync(crmUserId: _me);
        await SeedAsync(crmUserId: _colleague, agencyId: _myAgency);

        var page = await ReadAsync(
            new ListTelephonyCallsQuery(CrmUserId: _colleague), scope: [_myAgency]);

        page.Items.Should().ContainSingle().Which.CrmUserId.Should().Be(_colleague);
    }

    [Fact]
    public async Task The_agencyId_filter_narrows_to_one_branch_INSIDE_the_perimeter()
    {
        await SeedAsync(crmUserId: _colleague, agencyId: _myAgency);
        await SeedAsync(crmUserId: _colleague, agencyId: _otherAgency);

        // The filter composes with the perimeter rather than replacing it: asking for a branch
        // outside the scope must answer nothing, not grant it. That is the difference between a
        // filter and an authorization, and naming an id is the obvious way to try the second.
        (await ReadAsync(new ListTelephonyCallsQuery(AgencyId: _myAgency), scope: [_myAgency]))
            .Items.Should().HaveCount(1);

        (await ReadAsync(new ListTelephonyCallsQuery(AgencyId: _otherAgency), scope: [_myAgency]))
            .Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_direction_filter_separates_inbound_from_outbound()
    {
        await SeedAsync(crmUserId: _me, direction: TelCallDirection.Inbound);
        await SeedAsync(crmUserId: _me, direction: TelCallDirection.Outbound);

        var page = await ReadAsync(
            new ListTelephonyCallsQuery(Direction: TelCallDirection.Outbound));

        page.Items.Should().ContainSingle().Which.Direction.Should().Be("Outbound");
    }

    [Fact]
    public async Task The_status_filter_narrows_to_one_outcome_of_the_line()
    {
        await SeedAsync(crmUserId: _me, status: TelCallStatus.Completed);
        await SeedAsync(crmUserId: _me, status: TelCallStatus.Missed);

        var page = await ReadAsync(new ListTelephonyCallsQuery(Status: TelCallStatus.Missed));

        page.Items.Should().ContainSingle().Which.Status.Should().Be("Missed");
    }

    [Fact]
    public async Task The_attachment_filter_narrows_to_one_match_status()
    {
        await SeedAsync(crmUserId: _me, match: TelMatchStatus.Matched,
            entityType: TelEntityTypes.Lead, entityId: Guid.NewGuid());
        await SeedAsync(crmUserId: _me, match: TelMatchStatus.Ambiguous);

        var page = await ReadAsync(
            new ListTelephonyCallsQuery(Attachment: TelMatchStatus.Ambiguous));

        page.Items.Should().ContainSingle().Which.MatchStatus.Should().Be("Ambiguous");
    }

    [Fact]
    public async Task The_outcomeCode_filter_is_case_insensitive()
    {
        await SeedAsync(crmUserId: _me, outcomeCode: "REACHED");
        await SeedAsync(crmUserId: _me, outcomeCode: "NOT_REACHED");

        // The column holds the code upper-cased, so a screen or a bookmarked URL carrying
        // "reached" would otherwise answer an empty journal and read as "no such calls" rather
        // than as a casing mistake nobody can see in a query string.
        (await ReadAsync(new ListTelephonyCallsQuery(OutcomeCode: "reached")))
            .Items.Should().ContainSingle().Which.OutcomeCode.Should().Be("REACHED");

        (await ReadAsync(new ListTelephonyCallsQuery(OutcomeCode: "  ReAcHeD  ")))
            .Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_blank_outcomeCode_filters_nothing()
    {
        await SeedAsync(crmUserId: _me, outcomeCode: "REACHED");
        await SeedAsync(crmUserId: _me);

        // An empty query-string parameter is what a screen sends when its select is cleared, and
        // reading it as "calls whose outcome is the empty string" would answer nothing at all.
        (await ReadAsync(new ListTelephonyCallsQuery(OutcomeCode: "   "))).Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task EntityType_with_EntityId_gives_the_calls_of_ONE_record()
    {
        var lead = Guid.NewGuid();

        await SeedAsync(crmUserId: _me, match: TelMatchStatus.Matched,
            entityType: TelEntityTypes.Lead, entityId: lead);
        await SeedAsync(crmUserId: _me, match: TelMatchStatus.Matched,
            entityType: TelEntityTypes.Lead, entityId: Guid.NewGuid());
        await SeedAsync(crmUserId: _me, match: TelMatchStatus.Matched,
            entityType: TelEntityTypes.Customer, entityId: lead);

        var page = await ReadAsync(
            new ListTelephonyCallsQuery(EntityType: TelEntityTypes.Lead, EntityId: lead));

        // The TYPE is part of the key and not decoration: lead and client ids come from two
        // different schemas and nothing stops one guid from existing in both.
        page.Items.Should().ContainSingle().Which.EntityId.Should().Be(lead);
    }

    [Fact]
    public async Task HALF_an_entity_reference_is_REFUSED_and_never_widened()
    {
        await SeedAsync(crmUserId: _me, match: TelMatchStatus.Matched,
            entityType: TelEntityTypes.Lead, entityId: Guid.NewGuid());
        await SeedAsync(crmUserId: _me);

        // This used to be ignored, and the failure mode was a WIDENING — the one direction a
        // dropped filter must never go: a lead's history that lost one query parameter rendered
        // the caller's whole perimeter as if it were that lead's calls. Both halves now refuse,
        // which is what the aggregate's own Attach already does with the same pair.
        (await SendAsync(new ListTelephonyCallsQuery(EntityType: TelEntityTypes.Lead)))
            .Error.Should().Be(TelephonyErrors.EntityReferenceIncomplete);

        (await SendAsync(new ListTelephonyCallsQuery(EntityId: Guid.NewGuid())))
            .Error.Should().Be(TelephonyErrors.EntityReferenceIncomplete);

        // And the complete pair still works, so the refusal is about the HALF pair and not about
        // the filter.
        (await ReadAsync(new ListTelephonyCallsQuery(
                EntityType: TelEntityTypes.Lead, EntityId: Guid.NewGuid())))
            .Items.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_entityType_is_refused()
    {
        await SeedAsync(crmUserId: _me);

        var result = await SendAsync(new ListTelephonyCallsQuery(EntityType: "Invoice"));

        // Refused rather than ignored, because ignoring it is the dangerous direction: a caller
        // asking for one record's calls and getting the whole perimeter would read the answer as
        // that record's history.
        result.Error.Should().Be(TelephonyErrors.EntityTypeUnknown);
    }

    // ── The three queues ──────────────────────────────────────────────────────

    [Fact]
    public async Task ToAttach_is_anything_whose_attachment_is_not_settled()
    {
        await SeedAsync(crmUserId: _me, match: TelMatchStatus.Matched,
            entityType: TelEntityTypes.Lead, entityId: Guid.NewGuid());
        await SeedAsync(crmUserId: _me, match: TelMatchStatus.Ambiguous);
        await SeedAsync(crmUserId: _me, match: TelMatchStatus.Unknown);

        var page = await ReadAsync(
            new ListTelephonyCallsQuery(Queue: TelephonyCallQueue.ToAttach));

        // Ambiguous and Unknown together, because the WORK is the same — somebody has to say
        // which record this was about. Splitting them would give a screen two tabs for one job.
        page.Items.Should().HaveCount(2);
        page.Items.Should().NotContain(i => i.MatchStatus == "Matched");
    }

    [Fact]
    public async Task ToQualify_is_ended_and_not_wrapped_up()
    {
        await SeedAsync(crmUserId: _me, status: TelCallStatus.Completed);
        await SeedAsync(crmUserId: _me, status: TelCallStatus.InProgress);
        await SeedAsync(crmUserId: _me, status: TelCallStatus.Completed, outcomeCode: "REACHED");

        var page = await ReadAsync(
            new ListTelephonyCallsQuery(Queue: TelephonyCallQueue.ToQualify));

        // A call in flight is not outstanding paperwork — the agent is still speaking — and a
        // qualified one has been answered.
        page.Items.Should().ContainSingle().Which.WrappedUpAt.Should().BeNull();
    }

    [Fact]
    public async Task ToQualify_excludes_a_call_somebody_DISMISSED()
    {
        var dismissed = await SeedAsync(
            crmUserId: _me, status: TelCallStatus.Missed, dismissed: true);

        await SeedAsync(crmUserId: _me, status: TelCallStatus.Missed);

        var page = await ReadAsync(
            new ListTelephonyCallsQuery(Queue: TelephonyCallQueue.ToQualify));

        // The subtle third of the rule. A dismissal IS an answer — "I looked at this and nobody
        // needs calling back" — and an answered item is not outstanding work. Leaving it in would
        // make the closure button do nothing visible, so agents would reach for a wrap-up instead
        // and the institution would end up inventing an outcome code for "nothing" and counting
        // it in its contact figures.
        page.Items.Should().ContainSingle().Which.CallId.Should().NotBe(dismissed);
    }

    [Fact]
    public async Task ToAssign_is_attributed_to_nobody()
    {
        await SeedAsync(crmUserId: null);
        await SeedAsync(crmUserId: _me);

        var page = await ReadAsync(
            new ListTelephonyCallsQuery(Queue: TelephonyCallQueue.ToAssign));

        // The administrator's pile, and the repair is a user LINK rather than anything on the
        // call — which is why it is its own queue and not a filter a screen assembles.
        page.Items.Should().ContainSingle().Which.CrmUserId.Should().BeNull();
    }

    [Fact]
    public async Task A_queue_composes_with_another_filter()
    {
        await SeedAsync(crmUserId: _me, status: TelCallStatus.Completed,
            direction: TelCallDirection.Inbound);
        await SeedAsync(crmUserId: _me, status: TelCallStatus.Completed,
            direction: TelCallDirection.Outbound);

        var page = await ReadAsync(new ListTelephonyCallsQuery(
            Direction: TelCallDirection.Inbound, Queue: TelephonyCallQueue.ToQualify));

        // "To qualify, inbound, last week" is a reasonable question, and a queue that replaced the
        // filters instead of composing with them would answer a different one without saying so.
        page.Items.Should().ContainSingle().Which.Direction.Should().Be("Inbound");
    }

    // ── Ordering and pagination ───────────────────────────────────────────────

    [Fact]
    public async Task The_newest_call_comes_first()
    {
        await SeedAsync(crmUserId: _me, startedAt: Now.AddHours(-5));
        await SeedAsync(crmUserId: _me, startedAt: Now.AddMinutes(-5));

        var page = await ReadAsync();

        page.Items[0].StartedAt.Should().BeAfter(page.Items[1].StartedAt);
    }

    [Fact]
    public async Task Two_calls_that_began_in_the_same_instant_page_deterministically()
    {
        var at = Now.AddHours(-2);

        var first = await SeedAsync(crmUserId: _me, startedAt: at);
        var second = await SeedAsync(crmUserId: _me, startedAt: at);

        var pageOne = await ReadAsync(new ListTelephonyCallsQuery(Page: 1, PageSize: 1));
        var pageTwo = await ReadAsync(new ListTelephonyCallsQuery(Page: 2, PageSize: 1));

        // StartedAt alone is not a total order, and a non-deterministic tie-break is how a row
        // appears on two consecutive pages while another appears on none — the reader then sees a
        // duplicate and silently loses a call, with the totals still adding up.
        pageOne.Items.Should().ContainSingle();
        pageTwo.Items.Should().ContainSingle();

        new[] { pageOne.Items[0].CallId, pageTwo.Items[0].CallId }
            .Should().BeEquivalentTo(new[] { first, second });
    }

    [Fact]
    public async Task TotalCount_is_the_FILTERED_total_and_not_the_length_of_the_page()
    {
        for (var i = 0; i < 5; i++)
            await SeedAsync(crmUserId: _me, status: TelCallStatus.Missed);

        await SeedAsync(crmUserId: _me, status: TelCallStatus.Completed);

        var page = await ReadAsync(
            new ListTelephonyCallsQuery(Status: TelCallStatus.Missed, PageSize: 2));

        page.Items.Should().HaveCount(2);

        // Counted after the filters and the perimeter, never before: a total computed over rows
        // the caller may not see would paginate them into empty pages and tell a supervisor how
        // many calls another branch placed.
        page.TotalCount.Should().Be(5);
        page.TotalPages.Should().Be(3);
    }

    [Fact]
    public async Task A_page_below_one_is_clamped_rather_than_refused()
    {
        await SeedAsync(crmUserId: _me);

        var page = await ReadAsync(new ListTelephonyCallsQuery(Page: 0));

        // Clamped and not refused: page=0 is what an off-by-one in a paginator sends, and a
        // negative Skip is an exception from inside the provider rather than an answer.
        page.Page.Should().Be(1);
        page.Items.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(20, 20)]
    [InlineData(500, ListTelephonyCallsHandler.MaxPageSize)]
    public async Task The_page_size_is_clamped_to_one_and_to_the_ceiling(int asked, int expected)
    {
        await SeedAsync(crmUserId: _me);

        // A ceiling because this is the one telephony read somebody will try to drain in a loop;
        // a floor of one because pageSize=0 would otherwise return an empty page for ever and
        // PagedResult reads 0 as "un-paginated", which would make TotalPages claim a single page.
        (await ReadAsync(new ListTelephonyCallsQuery(PageSize: asked)))
            .PageSize.Should().Be(expected);
    }

    // ── What a row says ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_outcome_label_is_the_referentials_CURRENT_wording()
    {
        await SeedAsync(crmUserId: _me, outcomeCode: "REACHED");

        (await ReadAsync()).Items.Should().ContainSingle()
            .Which.OutcomeLabel.Should().Be("Joint");

        await using (var db = _factory.CreateContext())
        {
            var outcome = await db.TelCallOutcomes.AsTracking()
                .FirstAsync(o => o.Code == "REACHED", CancellationToken.None);

            outcome.Rename("Contacté par téléphone");
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var row = (await ReadAsync()).Items.Should().ContainSingle().Subject;

        // A lookup and not a stored copy, deliberately — and the indicators make the same choice.
        // An institution correcting a label expects last month's calls to read with the new
        // wording, and a frozen label would have one screen say "Joint" and the next "Contacté"
        // about the same call.
        row.OutcomeLabel.Should().Be("Contacté par téléphone");
        row.CountsAsContact.Should().BeTrue();
    }

    [Fact]
    public async Task A_call_whose_outcome_code_no_longer_exists_still_renders()
    {
        await SeedAsync(crmUserId: _me, outcomeCode: "RETIRED_BY_A_MIGRATION");

        var row = (await ReadAsync()).Items.Should().ContainSingle().Subject;

        // The pair has no foreign key, so a referential somebody edited by hand leaves dangling
        // codes. Degrading to a null label is the behaviour the journal needs: dropping the row
        // would silently shorten a period's history, and throwing would make one bad code take
        // out the whole page.
        row.OutcomeCode.Should().Be("RETIRED_BY_A_MIGRATION");
        row.OutcomeLabel.Should().BeNull();
        row.CountsAsContact.Should().BeNull();
    }

    [Fact]
    public async Task HasNote_says_a_note_exists_and_the_DTO_can_never_say_what_it_says()
    {
        await SeedAsync(crmUserId: _me, outcomeCode: "REACHED", note: "Rappeler le gérant.");
        await SeedAsync(crmUserId: _me, outcomeCode: "REACHED");

        var page = await ReadAsync();

        page.Items.Should().ContainSingle(i => i.HasNote);
        page.Items.Should().ContainSingle(i => !i.HasNote);

        var properties = typeof(TelephonyCallListItemDto)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        // Asserted over the TYPE and not over one row's values, because what has to hold is that
        // there is nowhere for a number or a note body to be put. A field added later for
        // convenience — a caller id "just for the tooltip" — would put the complete call history
        // of an institution's customers behind one permission, in a shape the CSV export then
        // carries out of the building.
        properties.Should().NotContain(
            n => n.Contains("Number", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("Phone", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("Caller", StringComparison.OrdinalIgnoreCase));

        // The note is referred to by its PRESENCE only: one boolean, and no string beside it.
        properties.Where(n => n.Contains("Note", StringComparison.OrdinalIgnoreCase))
            .Should().BeEquivalentTo(["HasNote"]);

        typeof(TelephonyCallListItemDto).GetProperty("HasNote")!
            .PropertyType.Should().Be(typeof(bool));
    }

    [Fact]
    public async Task CanListen_needs_the_permission_AND_an_available_recording()
    {
        await SeedAsync(crmUserId: _me, recording: TelRecordingStatus.Available);

        (await ReadAsync(mayListen: true)).Items.Should().ContainSingle()
            .Which.CanListen.Should().BeTrue();

        // Half one: the permission. A supervisor who may read the journal is not thereby allowed
        // to hear a customer's voice — the two are separate rights precisely so an institution can
        // grant the first without the second.
        (await ReadAsync(mayListen: false)).Items.Should().ContainSingle()
            .Which.CanListen.Should().BeFalse();
    }

    [Theory]
    [InlineData(TelRecordingStatus.NotRecorded)]
    [InlineData(TelRecordingStatus.Pending)]
    [InlineData(TelRecordingStatus.Failed)]
    [InlineData(TelRecordingStatus.Purged)]
    public async Task CanListen_is_false_when_there_is_no_audio_to_hear(TelRecordingStatus status)
    {
        await SeedAsync(crmUserId: _me, recording: status);

        // Half two: the audio. The flag exists so a screen does not have to probe the player for
        // a 404 — an offered button that always fails is worse than no button, and under Pending
        // or Purged the row is genuinely there with nothing behind it.
        (await ReadAsync(mayListen: true)).Items.Should().ContainSingle()
            .Which.CanListen.Should().BeFalse();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One call. <paramref name="crmUserId"/> is explicit at every call site, <c>null</c> meaning
    /// genuinely nobody — a sentinel default would make "attributed to nobody" and "left out of
    /// this test" the same argument, which is the one distinction half of these cases turn on.
    /// </summary>
    private async Task<Guid> SeedAsync(
        Guid? crmUserId,
        Guid? agencyId = null,
        TelCallStatus status = TelCallStatus.Completed,
        DateTimeOffset? startedAt = null,
        TelCallDirection direction = TelCallDirection.Inbound,
        TelMatchStatus match = TelMatchStatus.Unknown,
        string? entityType = null,
        Guid? entityId = null,
        string? outcomeCode = null,
        string? note = null,
        TelRecordingStatus recording = TelRecordingStatus.NotRecorded,
        bool dismissed = false,
        Guid? tenantId = null)
    {
        var tenant = tenantId ?? _tenant;

        await using var db = _factory.ContextFor(tenant);

        var protector = TelephonyTestHarness.Numbers();
        var from = protector.Protect("+22507111111");
        var to = protector.Protect("+22507000001");
        var at = startedAt ?? Now.AddHours(-1);

        var call = TelCall.Open(
            tenant, _connection, $"prov-{Guid.NewGuid()}",
            direction, TelCallStatus.Ringing,
            from.Encrypted, from.BlindIndex, to.Encrypted, to.BlindIndex,
            at, externalUserId: null, crmUserId: crmUserId, agencyId: agencyId);

        // Through the transitions rather than opened on a terminal status: EndedAt is what the
        // ToQualify queue reads, and it is written by the move and not by the constructor.
        if (status != TelCallStatus.Ringing)
        {
            if (status is TelCallStatus.Completed or TelCallStatus.InProgress)
                call.TryMoveTo(TelCallStatus.InProgress, at.AddSeconds(10));

            if (status != TelCallStatus.InProgress)
                call.TryMoveTo(status, at.AddSeconds(90));
        }

        if (match != TelMatchStatus.Unknown || entityType is not null)
            call.Attach(match, TelMatchMethod.Number, entityType, entityId)
                .IsSuccess.Should().BeTrue();

        if (outcomeCode is not null || note is not null)
        {
            // Encrypted, like the column holds it. A pass-through double here would let a reader
            // of this suite believe HasNote was computed over clear text.
            var noteEnc = note is null
                ? null
                : Encoding.UTF8.GetBytes(TelephonyTestHarness.Encryptor().Encrypt(note)!);

            call.WrapUp(outcomeCode ?? "REACHED", noteEnc, at.AddMinutes(5))
                .IsSuccess.Should().BeTrue();
        }

        if (dismissed)
            call.Dismiss(_me, at.AddMinutes(10)).IsSuccess.Should().BeTrue();

        if (recording != TelRecordingStatus.NotRecorded)
            call.SetRecordingStatus(recording);

        db.TelCalls.Add(call);
        await db.SaveChangesAsync(CancellationToken.None);

        return call.Id;
    }

    private async Task<PagedResult<TelephonyCallListItemDto>> ReadAsync(
        ListTelephonyCallsQuery? query = null,
        Guid[]? scope = null,
        bool mayListen = false)
    {
        var result = await SendAsync(query, scope, mayListen);

        result.IsSuccess.Should().BeTrue($"the query was refused with {result.Error}");

        return result.Value;
    }

    /// <summary>
    /// The handler, assembled. <paramref name="scope"/> defaults to the UNRESTRICTED null so a
    /// filter case asserts the filter and nothing else; every perimeter case passes its own.
    /// </summary>
    private async Task<Result<PagedResult<TelephonyCallListItemDto>>> SendAsync(
        ListTelephonyCallsQuery? query = null,
        Guid[]? scope = null,
        bool mayListen = false)
    {
        await using var db = _factory.CreateContext();

        return await new ListTelephonyCallsHandler(
                db,
                TelephonyTestHarness.AgencyScope(scope),
                TelephonyTestHarness.User(_tenant, _me),
                Accessor(mayListen))
            .Handle(query ?? new ListTelephonyCallsQuery(), CancellationToken.None);
    }

    /// <summary>
    /// A request carrying — or not carrying — the listening permission as a <c>permission</c>
    /// claim, which is how <c>AuthorizationPolicies</c> renders every one of them. Read off the
    /// token rather than re-queried, so the journal cannot disagree with what the player would
    /// have checked.
    /// </summary>
    private static IHttpContextAccessor Accessor(bool mayListen)
    {
        var http = new DefaultHttpContext();

        var claims = new List<Claim>();

        if (mayListen)
            claims.Add(new Claim("permission", Permissions.CanListenTelephonyRecording.Code));

        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);

        return accessor;
    }
}
