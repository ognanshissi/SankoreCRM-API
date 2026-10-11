namespace Sankore.Modules.Customers.Tests.Features.Household;

using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Household.Consumers;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

/// <summary>
/// The alert BNF-B-13 asks for: a departing household member leaves designations in
/// <c>ToReview</c>, so the rank they belonged to no longer totals 100%. Before this consumer the
/// event was published and nobody listened, which meant that state surfaced at claim time or not
/// at all.
/// </summary>
public sealed class HouseholdMemberRemovedAlertConsumerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _advisorId = Guid.NewGuid();
    private readonly Guid _removedBy = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly List<NotifyUserRequest> _sent = [];
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();

    public HouseholdMemberRemovedAlertConsumerTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenantId);

        _administration
            .NotifyUserAsync(Arg.Do<NotifyUserRequest>(_sent.Add), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    public void Dispose() => _factory.Dispose();

    private HouseholdMemberRemovedAlertConsumer Consumer()
    {
        var db = _factory.CreateContext();
        return new HouseholdMemberRemovedAlertConsumer(
            db,
            new InboxGuard(db),
            _administration,
            NullLogger<HouseholdMemberRemovedAlertConsumer>.Instance);
    }

    private async Task<Client> SeedClientAsync(Guid? advisorUserId)
    {
        await using var db = _factory.CreateContext();

        var client = TestClientFactory.Individual(
            _tenantId, _agencyId,
            clientNumber: "ABJ-PLT-2026-000042",
            advisorUserId: advisorUserId);

        return await TestClientFactory.SeedAsync(db, client);
    }

    private ConsumeContext<HouseholdMemberRemovedEvent> Context(
        Guid clientId,
        Guid? memberId = null,
        int designationsToReview = 2,
        IReadOnlyList<Guid>? holdings = null)
    {
        var evt = new HouseholdMemberRemovedEvent(
            TenantId: _tenantId,
            ClientId: clientId,
            MemberId: memberId ?? Guid.NewGuid(),
            DesignationsToReview: designationsToReview,
            AffectedHoldingIds: holdings ?? [Guid.NewGuid()],
            RemovedBy: _removedBy);

        var context = Substitute.For<ConsumeContext<HouseholdMemberRemovedEvent>>();
        context.Message.Returns(evt);
        context.MessageId.Returns(Guid.NewGuid());
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    [Fact]
    public async Task The_advisor_is_alerted()
    {
        var client = await SeedClientAsync(_advisorId);

        await Consumer().Consume(Context(client.Id));

        var sent = _sent.Should().ContainSingle().Subject;
        sent.UserId.Should().Be(_advisorId);
        sent.TenantId.Should().Be(_tenantId);
        sent.TemplateKey.Should().Be(HouseholdMemberRemovedAlertConsumer.TemplateKey);
    }

    [Fact]
    public async Task A_client_with_no_advisor_falls_back_to_the_agent_who_removed_the_member()
    {
        // The one outcome to avoid is no alert at all: nothing else in the system will ever
        // mention that a rank stopped totalling 100%.
        var client = await SeedClientAsync(advisorUserId: null);

        await Consumer().Consume(Context(client.Id));

        _sent.Should().ContainSingle().Which.UserId.Should().Be(_removedBy);
    }

    [Fact]
    public async Task The_alert_names_the_client_but_never_the_member()
    {
        // A rendered body reaches a mail provider and sits in the delivery log. Who belongs to a
        // client's household is exactly the kind of value this repo keeps out of both — the
        // counts are what the agent needs to act.
        var client = await SeedClientAsync(_advisorId);
        var memberId = Guid.NewGuid();

        await Consumer().Consume(Context(client.Id, memberId, designationsToReview: 3));

        var data = _sent.Should().ContainSingle().Subject.TemplateData!;
        data["client_number"].Should().Be("ABJ-PLT-2026-000042");
        data["designations_to_review"].Should().Be(3);
        data["holdings_affected"].Should().Be(1);
        data.Values.Select(v => v.ToString()).Should().NotContain(memberId.ToString());
    }

    [Fact]
    public async Task The_idempotency_key_is_the_member_not_the_message()
    {
        // Keyed on the departure, so a replayed event cannot produce a second alert even if the
        // inbox table were recreated.
        var client = await SeedClientAsync(_advisorId);
        var memberId = Guid.NewGuid();

        await Consumer().Consume(Context(client.Id, memberId));

        _sent.Should().ContainSingle().Which.IdempotencyKey
            .Should().Be($"{HouseholdMemberRemovedAlertConsumer.TemplateKey}-{memberId}");
    }

    [Fact]
    public async Task A_redelivered_message_alerts_once()
    {
        var client = await SeedClientAsync(_advisorId);
        var context = Context(client.Id);

        await Consumer().Consume(context);
        await Consumer().Consume(context);

        _sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_vanished_client_is_not_an_incident()
    {
        var act = async () => await Consumer().Consume(Context(Guid.NewGuid()));

        await act.Should().NotThrowAsync();
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_send_still_commits_the_inbox_row()
    {
        // NotifyUserAsync returning false is ordinary — a deactivated advisor, a user with no
        // address. Rolling the inbox row back would have the broker redeliver for ever against a
        // condition only an administrator can change.
        _administration
            .NotifyUserAsync(Arg.Any<NotifyUserRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var client = await SeedClientAsync(_advisorId);

        await Consumer().Consume(Context(client.Id));

        await using var db = _factory.CreateContext();
        db.InboxMessages.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_SYSTEM_placeholder_tenant_is_skipped()
    {
        var client = await SeedClientAsync(_advisorId);

        var evt = new HouseholdMemberRemovedEvent(
            TenantId: Guid.Empty,
            ClientId: client.Id,
            MemberId: Guid.NewGuid(),
            DesignationsToReview: 1,
            AffectedHoldingIds: [Guid.NewGuid()],
            RemovedBy: _removedBy);

        var context = Substitute.For<ConsumeContext<HouseholdMemberRemovedEvent>>();
        context.Message.Returns(evt);
        context.MessageId.Returns(Guid.NewGuid());
        context.CancellationToken.Returns(CancellationToken.None);

        await Consumer().Consume(context);

        _sent.Should().BeEmpty();
    }
}
