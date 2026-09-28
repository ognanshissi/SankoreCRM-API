namespace Sankore.Modules.Customers.Tests.Features.Timeline;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Features.Timeline.Segments;
using Sankore.Modules.Customers.Features.Timeline.Segments.AssignSegments;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class AssignSegmentsHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 3, 0, 0, TimeSpan.Zero);

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private AssignSegmentsHandler BuildHandler(
        string rulesJson,
        out RecordingTimelineEventPublisher publisher)
    {
        publisher = new RecordingTimelineEventPublisher();
        return new AssignSegmentsHandler(
            _factory.CreateContext(),
            TestDoubles.Settings(TenantId, (CustomerSettingKeys.SegmentRulesJson, rulesJson)),
            publisher,
            new FixedTimeProvider(Now),
            NullLogger<AssignSegmentsHandler>.Instance);
    }

    private static string Rules(params SegmentRuleDefinition[] rules)
        => SegmentRuleEvaluation.Serialize(rules);

    private async Task<Client> SeedAsync(Client client, params DateTimeOffset[] timelineFacts)
    {
        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);

        var index = 0;
        foreach (var at in timelineFacts)
            seed.ClientTimelineEntries.Add(TimelineFixtures.Entry(
                client.TenantId, client.Id, at, dedupKey: $"{client.Id:D}-fact-{index++}"));

        await seed.SaveChangesAsync();
        return client;
    }

    // ── Priority & first match wins ──────────────────────────────────────────

    [Fact]
    public async Task Applies_the_first_matching_rule_in_ascending_priority_order()
    {
        // Both rules match a two-year-old client; priority 10 must win over priority 20.
        var client = await SeedAsync(
            TimelineFixtures.Active(TenantId, AgencyId, UserId).BackdatedTo(Now.AddDays(-730)));

        var handler = BuildHandler(
            Rules(
                new SegmentRuleDefinition("R-PREMIUM", 10, "PREMIUM", MinTenureDays: 365),
                new SegmentRuleDefinition("R-ALL", 20, "STANDARD")),
            out _);

        var result = await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SegmentsChanged.Should().Be(1);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == client.Id)).SegmentCode.Should().Be("PREMIUM");
    }

    [Fact]
    public async Task Falls_through_to_the_catch_all_rule_when_no_criterion_matches()
    {
        var client = await SeedAsync(
            TimelineFixtures.Active(TenantId, AgencyId, UserId).BackdatedTo(Now.AddDays(-10)));

        var handler = BuildHandler(
            Rules(
                new SegmentRuleDefinition("R-PREMIUM", 10, "PREMIUM", MinTenureDays: 365),
                new SegmentRuleDefinition("R-ALL", 20, "STANDARD")),
            out _);

        await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == client.Id)).SegmentCode.Should().Be("STANDARD");
    }

    [Fact]
    public async Task Leaves_the_current_segment_untouched_when_no_rule_matches()
    {
        var client = await SeedAsync(
            TimelineFixtures.Active(TenantId, AgencyId, UserId).BackdatedTo(Now.AddDays(-10)));

        var handler = BuildHandler(
            Rules(new SegmentRuleDefinition("R-PREMIUM", 10, "PREMIUM", MinTenureDays: 3650)),
            out var publisher);

        var result = await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        result.Value.ClientsEvaluated.Should().Be(1);
        result.Value.SegmentsChanged.Should().Be(0);
        publisher.Published.Should().BeEmpty();

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == client.Id)).SegmentCode.Should().BeNull();
    }

    [Fact]
    public async Task Does_not_republish_when_the_matching_rule_confirms_the_current_segment()
    {
        var client = TimelineFixtures.Active(TenantId, AgencyId, UserId).BackdatedTo(Now.AddDays(-730));
        client.SetSegment("PREMIUM", Now.AddDays(-1));
        await SeedAsync(client);

        var handler = BuildHandler(
            Rules(new SegmentRuleDefinition("R-PREMIUM", 10, "PREMIUM", MinTenureDays: 365)),
            out var publisher);

        var result = await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        result.Value.SegmentsChanged.Should().Be(0);
        publisher.OfType<ClientSegmentChangedEvent>().Should().BeEmpty();
    }

    // ── History & event ──────────────────────────────────────────────────────

    [Fact]
    public async Task Closes_the_running_history_row_opens_a_new_one_and_publishes_the_change()
    {
        var client = TimelineFixtures.Active(TenantId, AgencyId, UserId).BackdatedTo(Now.AddDays(-730));
        client.SetSegment("STANDARD", Now.AddDays(-100));

        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(client);
            seed.ClientSegmentHistories.Add(ClientSegmentHistory.Open(
                TenantId, client.Id, "STANDARD", Now.AddDays(-100), "R-ALL"));
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(
            Rules(new SegmentRuleDefinition("R-PREMIUM", 10, "PREMIUM", MinTenureDays: 365)),
            out var publisher);

        await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        publisher.OfType<ClientSegmentChangedEvent>().Should().ContainSingle()
            .Which.Should().Match<ClientSegmentChangedEvent>(e =>
                e.TenantId == TenantId && e.ClientId == client.Id
                && e.PreviousSegment == "STANDARD" && e.NewSegment == "PREMIUM");

        await using var assertions = _factory.CreateContext();
        var history = await assertions.ClientSegmentHistories
            .Where(h => h.ClientId == client.Id)
            .OrderBy(h => h.ValidFrom)
            .ToListAsync();

        history.Should().HaveCount(2);
        history[0].SegmentCode.Should().Be("STANDARD");
        history[0].ValidTo.Should().Be(Now, "the previous membership is closed, never overwritten");
        history[1].SegmentCode.Should().Be("PREMIUM");
        history[1].ValidTo.Should().BeNull();
        history[1].RuleCode.Should().Be("R-PREMIUM");
    }

    // ── Non-evaluable rules ──────────────────────────────────────────────────

    [Fact]
    public async Task Ignores_a_rule_that_needs_outstanding_data_and_reports_it_as_skipped()
    {
        var client = await SeedAsync(
            TimelineFixtures.Active(TenantId, AgencyId, UserId).BackdatedTo(Now.AddDays(-730)));

        var handler = BuildHandler(
            Rules(
                // Highest priority, but unevaluable: it must NOT win, and must not be applied
                // with zeroed data either.
                new SegmentRuleDefinition("R-WEALTH", 1, "WEALTH", RequiresOutstandingData: true),
                new SegmentRuleDefinition("R-ALL", 20, "STANDARD")),
            out _);

        var result = await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        result.Value.RulesSkipped.Should().Be(1);
        result.Value.RulesApplied.Should().Be(1);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == client.Id)).SegmentCode.Should().Be("STANDARD");
    }

    [Fact]
    public async Task Touches_nothing_when_every_rule_needs_outstanding_data()
    {
        var client = await SeedAsync(TimelineFixtures.Active(TenantId, AgencyId, UserId));

        var handler = BuildHandler(
            Rules(new SegmentRuleDefinition("R-WEALTH", 1, "WEALTH", RequiresOutstandingData: true)),
            out var publisher);

        var result = await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        result.Value.ClientsEvaluated.Should().Be(0);
        result.Value.RulesSkipped.Should().Be(1);
        publisher.Published.Should().BeEmpty();

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == client.Id)).SegmentCode.Should().BeNull();
    }

    [Fact]
    public async Task Touches_nothing_when_the_rule_set_is_malformed()
    {
        await SeedAsync(TimelineFixtures.Active(TenantId, AgencyId, UserId));

        var handler = BuildHandler("{ this is not a rule array", out var publisher);

        var result = await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a broken setting must not take the nightly job down");
        result.Value.ClientsEvaluated.Should().Be(0);
        publisher.Published.Should().BeEmpty();
    }

    // ── Criteria ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Matches_on_the_days_since_the_last_timeline_fact()
    {
        var active = await SeedAsync(
            TimelineFixtures.Active(TenantId, AgencyId, UserId, "AG000001-2026-000001")
                .BackdatedTo(Now.AddDays(-400)),
            Now.AddDays(-5));

        var dormant = await SeedAsync(
            TimelineFixtures.Active(TenantId, AgencyId, UserId, "AG000001-2026-000002")
                .BackdatedTo(Now.AddDays(-400)),
            Now.AddDays(-400));

        var handler = BuildHandler(
            Rules(
                new SegmentRuleDefinition("R-ACTIVE", 10, "ACTIVE", MaxDaysSinceLastActivity: 30),
                new SegmentRuleDefinition("R-DORMANT", 20, "DORMANT", MinDaysSinceLastActivity: 90)),
            out _);

        await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == active.Id)).SegmentCode.Should().Be("ACTIVE");
        (await assertions.Clients.SingleAsync(c => c.Id == dormant.Id)).SegmentCode.Should().Be("DORMANT");
    }

    [Fact]
    public async Task Treats_a_client_with_no_timeline_fact_as_dormant_but_never_as_recently_active()
    {
        var silent = await SeedAsync(
            TimelineFixtures.Active(TenantId, AgencyId, UserId).BackdatedTo(Now.AddDays(-400)));

        var handler = BuildHandler(
            Rules(
                new SegmentRuleDefinition("R-ACTIVE", 10, "ACTIVE", MaxDaysSinceLastActivity: 30),
                new SegmentRuleDefinition("R-DORMANT", 20, "DORMANT", MinDaysSinceLastActivity: 90)),
            out _);

        await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == silent.Id)).SegmentCode.Should().Be("DORMANT");
    }

    [Fact]
    public async Task Matches_on_the_required_kyc_status_and_risk_level()
    {
        var approved = await SeedAsync(
            TimelineFixtures.Active(TenantId, AgencyId, UserId, "AG000001-2026-000001"));
        var pending = await SeedAsync(
            TimelineFixtures.PendingKyc(TenantId, AgencyId, UserId, "AG000001-2026-000002"));

        var handler = BuildHandler(
            Rules(new SegmentRuleDefinition("R-KYC-OK", 10, "ELIGIBLE", RequiredKycStatus: "approved")),
            out _);

        await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == approved.Id)).SegmentCode.Should().Be("ELIGIBLE");
        (await assertions.Clients.SingleAsync(c => c.Id == pending.Id)).SegmentCode.Should().BeNull();
    }

    // ── Scope ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Skips_archived_and_merged_clients()
    {
        var archived = await SeedAsync(
            TimelineFixtures.Archived(TenantId, AgencyId, UserId).BackdatedTo(Now.AddDays(-730)));

        var handler = BuildHandler(Rules(new SegmentRuleDefinition("R-ALL", 10, "STANDARD")), out _);

        var result = await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        result.Value.ClientsEvaluated.Should().Be(0);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == archived.Id)).SegmentCode.Should().BeNull();
    }

    [Fact]
    public async Task Never_segments_a_client_of_another_tenant()
    {
        var mine = await SeedAsync(
            TimelineFixtures.Active(TenantId, AgencyId, UserId, "AG000001-2026-000001"));
        var foreign = await SeedAsync(
            TimelineFixtures.Active(OtherTenantId, AgencyId, UserId, "AG000001-2026-000002"));

        var handler = BuildHandler(Rules(new SegmentRuleDefinition("R-ALL", 10, "STANDARD")), out _);

        var result = await handler.Handle(new AssignSegmentsCommand(TenantId), CancellationToken.None);

        result.Value.ClientsEvaluated.Should().Be(1);

        await using var assertions = _factory.CreateContext();
        var all = await assertions.Clients.IgnoreQueryFilters().ToListAsync();

        all.Single(c => c.Id == mine.Id).SegmentCode.Should().Be("STANDARD");
        all.Single(c => c.Id == foreign.Id).SegmentCode.Should().BeNull();
    }

    // ── Rule parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void Round_trips_a_rule_set_through_the_tenant_setting_ordered_by_priority()
    {
        var json = SegmentRuleEvaluation.Serialize(
        [
            new SegmentRuleDefinition("R-B", 20, "STANDARD"),
            new SegmentRuleDefinition("R-A", 10, "PREMIUM", MinTenureDays: 365),
        ]);

        SegmentRuleEvaluation.TryParse(json, out var parsed, out var error).Should().BeTrue();
        error.Should().BeNull();
        parsed.Select(r => r.Code).Should().ContainInOrder("R-A", "R-B");
        parsed[0].MinTenureDays.Should().Be(365);
    }

    [Fact]
    public void Accepts_camel_case_rules_written_by_hand()
    {
        const string Json = """
        [{"code":"R-VIP","priority":5,"segmentCode":"VIP","minTenureDays":1000,
          "requiredKycStatus":"Approved","requiresOutstandingData":false}]
        """;

        SegmentRuleEvaluation.TryParse(Json, out var parsed, out _).Should().BeTrue();
        parsed.Should().ContainSingle();
        parsed[0].SegmentCode.Should().Be("VIP");
        parsed[0].IsEvaluable().Should().BeTrue();
    }

    [Fact]
    public void Reports_a_malformed_rule_set_instead_of_throwing()
    {
        SegmentRuleEvaluation.TryParse("{ nope", out var parsed, out var error).Should().BeFalse();
        parsed.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Serializes_a_rule_set_as_a_json_array()
    {
        var json = SegmentRuleEvaluation.Serialize([new SegmentRuleDefinition("R-A", 1, "A")]);
        JsonDocument.Parse(json).RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }
}
