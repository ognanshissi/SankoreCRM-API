namespace Sankore.Modules.Customers.Tests.Features.Timeline;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Features.Timeline.Consumers;
using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Leads.PublicApi;
using Xunit;

public sealed class TimelineConsumerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TargetAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid ClientId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid AbsorbedId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid GroupId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid LeadId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly FakeTimelineInboxGuard _inbox = new();

    public void Dispose() => _factory.Dispose();

    private static ConsumeContext<TEvent> Context<TEvent>(TEvent evt, Guid? messageId = null)
        where TEvent : class
    {
        var context = Substitute.For<ConsumeContext<TEvent>>();
        context.Message.Returns(evt);
        context.MessageId.Returns(messageId);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    private ClientTimelineProjector RealProjector()
        => new(_factory.CreateContext(), NullLogger<ClientTimelineProjector>.Instance);

    private static ClientCreatedEvent CreatedEvent(Guid? sourceLeadId = null, string clientType = "Individual")
        => new(TenantId, ClientId, "AG000001-2026-000001", clientType, AgencyId, null, sourceLeadId, UserId);

    // ── Idempotency ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_replayed_event_creates_a_single_timeline_entry()
    {
        var evt = new ClientActivatedEvent(TenantId, ClientId);
        var projector = RealProjector();

        // Same event, three deliveries: two under the same transport message id (the inbox
        // stops them) and one under a fresh id (the dedup key stops that one).
        var messageId = Guid.NewGuid();
        await new ClientActivatedTimelineConsumer(_inbox, projector).Consume(Context(evt, messageId));
        await new ClientActivatedTimelineConsumer(_inbox, projector).Consume(Context(evt, messageId));
        await new ClientActivatedTimelineConsumer(_inbox, projector).Consume(Context(evt, Guid.NewGuid()));

        await using var assertions = _factory.CreateContext();
        (await assertions.ClientTimelineEntries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_inbox_stops_a_redelivery_before_the_projector_is_even_called()
    {
        var evt = new ClientSuspendedEvent(TenantId, ClientId, "Revue de conformité", UserId);
        var projector = new RecordingTimelineProjector();
        var messageId = Guid.NewGuid();

        await new ClientSuspendedTimelineConsumer(_inbox, projector).Consume(Context(evt, messageId));
        await new ClientSuspendedTimelineConsumer(_inbox, projector).Consume(Context(evt, messageId));

        projector.Calls.Should().HaveCount(1);
        _inbox.Calls.Should().Be(2, "the guard is asked on every delivery, including the rejected one");
    }

    [Fact]
    public async Task The_same_event_id_in_two_tenants_is_projected_once_per_tenant()
    {
        var projector = new RecordingTimelineProjector();

        var forTenantA = new ClientActivatedEvent(TenantId, ClientId);
        var forTenantB = new ClientActivatedEvent(OtherTenantId, ClientId);
        var sharedMessageId = Guid.NewGuid();

        await new ClientActivatedTimelineConsumer(_inbox, projector).Consume(Context(forTenantA, sharedMessageId));
        await new ClientActivatedTimelineConsumer(_inbox, projector).Consume(Context(forTenantB, sharedMessageId));

        projector.Calls.Should().HaveCount(2);
        projector.Calls.Select(c => c.TenantId).Should().BeEquivalentTo(new[] { TenantId, OtherTenantId });
    }

    // ── Lifecycle facts ─────────────────────────────────────────────────────

    [Fact]
    public async Task Projects_the_creation_of_a_client_without_any_sensitive_value()
    {
        var projector = new RecordingTimelineProjector();

        await new ClientCreatedTimelineConsumer(
                _inbox, projector, NullLogger<ClientCreatedTimelineConsumer>.Instance)
            .Consume(Context(CreatedEvent()));

        var call = projector.Calls.Should().ContainSingle().Subject;
        call.SourceModule.Should().Be(TimelineSourceModules.Customers);
        call.EntryType.Should().Be(ClientTimelineEntryTypes.ClientCreated);
        call.ReferenceType.Should().Be("Client");
        TimelineSummaryGuard.LooksSensitive(call.Summary).Should().BeFalse();
    }

    [Fact]
    public async Task Projects_a_merge_onto_the_surviving_client_only()
    {
        var projector = new RecordingTimelineProjector();
        var evt = new ClientsMergedEvent(TenantId, ClientId, AbsorbedId, UserId);

        await new ClientsMergedTimelineConsumer(_inbox, projector).Consume(Context(evt));

        var call = projector.Calls.Should().ContainSingle().Subject;
        call.ClientId.Should().Be(ClientId, "the absorbed record's own entries are re-parented by the merge executor");
        call.ReferenceId.Should().Be(AbsorbedId.ToString("D"));
    }

    [Fact]
    public async Task Projects_a_transfer_with_both_agencies_and_the_advisor_reset()
    {
        var projector = new RecordingTimelineProjector();
        var evt = new ClientTransferredEvent(TenantId, ClientId, AgencyId, TargetAgencyId, null, UserId);

        await new ClientTransferredTimelineConsumer(_inbox, projector).Consume(Context(evt));

        var call = projector.Calls.Should().ContainSingle().Subject;
        call.Summary.Should().Contain(AgencyId.ToString("D")).And.Contain(TargetAgencyId.ToString("D"));
        call.Summary.Should().Contain("réinitialisé");
        call.ReferenceId.Should().Be(TargetAgencyId.ToString("D"));
    }

    [Fact]
    public async Task Projects_an_archival_at_the_archival_instant_not_at_the_publication_instant()
    {
        var projector = new RecordingTimelineProjector();
        var archivedAt = DateTimeOffset.UtcNow.AddDays(-3);
        var evt = new ClientArchivedEvent(TenantId, ClientId, "Client décédé", UserId, archivedAt);

        await new ClientArchivedTimelineConsumer(_inbox, projector).Consume(Context(evt));

        projector.Calls.Should().ContainSingle().Which.OccurredAt.Should().Be(archivedAt);
    }

    [Theory]
    [InlineData("Joined", "Adhésion")]
    [InlineData("Left", "Sortie")]
    [InlineData("RoleChanged", "Rôle de bureau")]
    public async Task Projects_a_group_membership_change_onto_the_member(string change, string expected)
    {
        var projector = new RecordingTimelineProjector();
        var evt = new GroupMembershipChangedEvent(TenantId, GroupId, ClientId, change, "President");

        await new GroupMembershipChangedTimelineConsumer(_inbox, projector).Consume(Context(evt));

        var call = projector.Calls.Should().ContainSingle().Subject;
        call.ClientId.Should().Be(ClientId);
        call.Summary.Should().Contain(expected);
        call.ReferenceType.Should().Be("ClientGroup");
    }

    [Fact]
    public async Task Projects_a_first_segment_assignment_differently_from_a_change()
    {
        var projector = new RecordingTimelineProjector();

        await new ClientSegmentChangedTimelineConsumer(_inbox, projector)
            .Consume(Context(new ClientSegmentChangedEvent(TenantId, ClientId, null, "STANDARD")));

        await new ClientSegmentChangedTimelineConsumer(_inbox, projector)
            .Consume(Context(new ClientSegmentChangedEvent(TenantId, ClientId, "STANDARD", "PREMIUM")));

        projector.Calls.Should().HaveCount(2);
        projector.Calls[0].Summary.Should().Contain("Segment attribué");
        projector.Calls[1].Summary.Should().Contain("STANDARD → PREMIUM");
    }

    // ── Lead history import (F13.29) ────────────────────────────────────────

    [Fact]
    public async Task Imports_the_commercial_history_of_the_source_lead()
    {
        var history = new LeadHistoryItem[]
        {
            new("LEAD_ACTIVITY_CALL", DateTimeOffset.UtcNow.AddDays(-20),
                "Appel : Premier contact — issue : Reached", "LeadActivity", Guid.NewGuid().ToString("D")),
            new("LEAD_VISIT", DateTimeOffset.UtcNow.AddDays(-10),
                "Visite terrain : Évaluation boutique", "LeadActivity", Guid.NewGuid().ToString("D")),
            new("LEAD_REMINDER", DateTimeOffset.UtcNow.AddDays(-5),
                "Relance « Rappeler pour le dossier » — Completed", "LeadReminder", Guid.NewGuid().ToString("D")),
        };

        var leads = new StubLeadsModule(history);
        var projector = new RecordingTimelineProjector();

        await new ClientCreatedTimelineConsumer(
                _inbox, projector, NullLogger<ClientCreatedTimelineConsumer>.Instance, leads)
            .Consume(Context(CreatedEvent(LeadId)));

        leads.HistoryCalls.Should().Be(1);
        projector.Calls.Should().HaveCount(4, "one CLIENT_CREATED entry plus the three imported facts");

        var imported = projector.Calls.Where(c => c.SourceModule == TimelineSourceModules.Leads).ToList();
        imported.Should().HaveCount(3);
        imported.Should().OnlyContain(c => c.ClientId == ClientId);
        imported.Select(c => c.EntryType).Should()
            .BeEquivalentTo(new[] { "LEAD_ACTIVITY_CALL", "LEAD_VISIT", "LEAD_REMINDER" });
        imported.Select(c => c.DedupKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Imports_the_lead_history_only_once_even_if_the_creation_event_is_replayed()
    {
        var referenceId = Guid.NewGuid().ToString("D");
        var occurredAt = DateTimeOffset.UtcNow.AddDays(-7);
        var history = new LeadHistoryItem[]
        {
            new("LEAD_NOTE", occurredAt, "Note : Client intéressé par un prêt", "LeadActivity", referenceId),
        };

        var projector = RealProjector();
        var evt = CreatedEvent(LeadId);

        // Second delivery under a NEW message id: the inbox lets it through, the dedup key
        // must be what prevents the duplicate.
        await new ClientCreatedTimelineConsumer(
                _inbox, projector, NullLogger<ClientCreatedTimelineConsumer>.Instance,
                new StubLeadsModule(history))
            .Consume(Context(evt, Guid.NewGuid()));

        await new ClientCreatedTimelineConsumer(
                _inbox, projector, NullLogger<ClientCreatedTimelineConsumer>.Instance,
                new StubLeadsModule(history))
            .Consume(Context(evt, Guid.NewGuid()));

        await using var assertions = _factory.CreateContext();
        var entries = await assertions.ClientTimelineEntries.ToListAsync();

        entries.Should().HaveCount(2, "CLIENT_CREATED plus the single imported note");
        entries.Count(e => e.SourceModule == TimelineSourceModules.Leads).Should().Be(1);
    }

    [Fact]
    public async Task Does_not_call_the_leads_module_when_the_client_has_no_source_lead()
    {
        var leads = new StubLeadsModule();
        var projector = new RecordingTimelineProjector();

        await new ClientCreatedTimelineConsumer(
                _inbox, projector, NullLogger<ClientCreatedTimelineConsumer>.Instance, leads)
            .Consume(Context(CreatedEvent()));

        leads.HistoryCalls.Should().Be(0);
        projector.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Keeps_the_creation_entry_when_the_lead_history_cannot_be_read()
    {
        var projector = new RecordingTimelineProjector();

        await new ClientCreatedTimelineConsumer(
                _inbox, projector, NullLogger<ClientCreatedTimelineConsumer>.Instance,
                new StubLeadsModule(throwOnHistory: true))
            .Consume(Context(CreatedEvent(LeadId)));

        projector.Calls.Should().ContainSingle()
            .Which.EntryType.Should().Be(ClientTimelineEntryTypes.ClientCreated);
    }

    [Fact]
    public async Task Works_without_the_leads_module_registered_at_all()
    {
        var projector = new RecordingTimelineProjector();

        await new ClientCreatedTimelineConsumer(
                _inbox, projector, NullLogger<ClientCreatedTimelineConsumer>.Instance)
            .Consume(Context(CreatedEvent(LeadId)));

        projector.Calls.Should().ContainSingle()
            .Which.EntryType.Should().Be(ClientTimelineEntryTypes.ClientCreated);
    }

    [Fact]
    public async Task Never_writes_a_sensitive_value_even_when_a_lead_summary_carries_one()
    {
        // A lead subject typed by an agent is exactly where a phone number leaks in.
        var history = new LeadHistoryItem[]
        {
            new("LEAD_NOTE", DateTimeOffset.UtcNow.AddDays(-2),
                "Note : rappeler au 0711223344, pièce CI0123456789, mail jean@example.com",
                "LeadActivity", Guid.NewGuid().ToString("D")),
        };

        await new ClientCreatedTimelineConsumer(
                _inbox, RealProjector(), NullLogger<ClientCreatedTimelineConsumer>.Instance,
                new StubLeadsModule(history))
            .Consume(Context(CreatedEvent(LeadId)));

        await using var assertions = _factory.CreateContext();
        var imported = await assertions.ClientTimelineEntries
            .SingleAsync(e => e.SourceModule == TimelineSourceModules.Leads);

        imported.Summary.Should().NotContain("0711223344");
        imported.Summary.Should().NotContain("CI0123456789");
        imported.Summary.Should().NotContain("jean@example.com");
    }
}
