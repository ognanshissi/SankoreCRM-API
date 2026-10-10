namespace Sankore.Modules.Customers.Tests.Features.Holdings;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.Consumers;
using Sankore.Modules.Customers.Features.Holdings.Sync;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.Features.Lifecycle;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// BNF-B-16's return path: the back office answers, and the holding moves to <c>Active</c> or
/// <c>Rejected</c>. Before this the chain stopped at <c>PendingSync</c> and nothing ever moved it.
/// </summary>
public sealed class HoldingSyncOutcomeTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly FakeInboxGuard _inbox = new();
    private readonly Guid _clientId;

    public HoldingSyncOutcomeTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(_tenant, _agency);
        db.Clients.Add(client);
        db.SaveChanges();
        _clientId = client.Id;
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    /// <summary>A holding taken as far as PendingSync, the way an agent would.</summary>
    private async Task<Guid> SeedPendingSyncAsync()
    {
        var line = TestHoldingFactory.Line();

        await using var db = _factory.CreateContext();
        var holding = TestHoldingFactory.Draft(_tenant, _clientId, line, _actor, DateTimeOffset.UtcNow);

        holding.Designate(
            [TestHoldingFactory.Beneficiary(100m)], line.AllowedRoles, _actor, DateTimeOffset.UtcNow)
            .IsSuccess.Should().BeTrue();

        holding.RequestSignature([], DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();
        holding.Sign("doc://signature/1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            .IsSuccess.Should().BeTrue();
        holding.SubmitForSync(DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();

        db.ProductHoldings.Add(holding);
        await db.SaveChangesAsync(CancellationToken.None);
        return holding.Id;
    }

    private HoldingSyncService Sync(CustomersDbContext db)
        => TestHoldingSync.For(db, _tenant);

    private async Task ConfirmAsync(
        Guid crmId, string? externalRef = "POL-00042", string entityType = "Policy",
        Guid? messageId = null)
    {
        await using var db = _factory.CreateContext();

        var consumer = new HoldingSyncConfirmedConsumer(
            Sync(db), _inbox, NullLogger<HoldingSyncConfirmedConsumer>.Instance);

        await consumer.Consume(Delivered(new IntegrationCommandSucceededEvent(
            TenantId: _tenant,
            CommandId: Guid.NewGuid(),
            ConnectionId: Guid.NewGuid(),
            CommandType: "Orass",
            EntityType: entityType,
            CrmId: crmId,
            ExternalRef: externalRef,
            SucceededAt: DateTimeOffset.UtcNow), messageId ?? Guid.NewGuid()));
    }

    private async Task RejectAsync(
        Guid crmId, string family = "Functional", string code = "BENEFICIARY_UNDERAGE",
        string? detail = "Le bénéficiaire de rang 1 est mineur sans tuteur.",
        string entityType = "Policy", Guid? messageId = null)
    {
        await using var db = _factory.CreateContext();

        var consumer = new HoldingSyncRejectedConsumer(
            Sync(db), _inbox, NullLogger<HoldingSyncRejectedConsumer>.Instance);

        await consumer.Consume(Delivered(new IntegrationCommandRejectedEvent(
            TenantId: _tenant,
            CommandId: Guid.NewGuid(),
            ConnectionId: Guid.NewGuid(),
            CommandType: "SubscribePolicy",
            EntityType: entityType,
            CrmId: crmId,
            ErrorFamily: family,
            ErrorCode: code,
            Detail: detail,
            Attempts: 1,
            RejectedAt: DateTimeOffset.UtcNow), messageId ?? Guid.NewGuid()));
    }

    private static ConsumeContext<T> Delivered<T>(T message, Guid messageId) where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(message);
        context.MessageId.Returns(messageId);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    private async Task<ProductHolding> ReloadAsync(Guid holdingId)
    {
        await using var db = _factory.CreateContext();
        return await db.ProductHoldings
            .Include(h => h.Designations)
            .FirstAsync(h => h.Id == holdingId, CancellationToken.None);
    }

    // ── acceptance ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_accepted_subscription_becomes_active_with_its_reference()
    {
        var holdingId = await SeedPendingSyncAsync();

        await ConfirmAsync(holdingId);

        var holding = await ReloadAsync(holdingId);
        holding.Status.Should().Be(HoldingStatus.Active);
        holding.ExternalRef.Should().Be("POL-00042");
        holding.SyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_success_with_no_reference_leaves_it_waiting()
    {
        // ExternalRef is the only link between the two systems. An Active holding without one
        // cannot be reconciled (BNF-B-17), which is a worse state than one visibly waiting.
        var holdingId = await SeedPendingSyncAsync();

        await ConfirmAsync(holdingId, externalRef: null);

        (await ReloadAsync(holdingId)).Status.Should().Be(HoldingStatus.PendingSync);
    }

    [Fact]
    public async Task A_redelivered_success_changes_nothing()
    {
        var holdingId = await SeedPendingSyncAsync();
        var messageId = Guid.NewGuid();

        await ConfirmAsync(holdingId, messageId: messageId);
        var first = (await ReloadAsync(holdingId)).SyncedAt;

        // The same message again — stopped by the inbox — and then a DIFFERENT message carrying
        // the same outcome, which is the case the inbox cannot catch and the service must.
        await ConfirmAsync(holdingId, messageId: messageId);
        await ConfirmAsync(holdingId);

        var holding = await ReloadAsync(holdingId);
        holding.Status.Should().Be(HoldingStatus.Active);
        holding.SyncedAt.Should().Be(first);
    }

    [Fact]
    public async Task A_second_DIFFERENT_reference_is_refused_rather_than_overwriting()
    {
        // Two external references for one contract means one of the two systems is wrong, and
        // picking a side silently is what a reconciliation cannot undo.
        var holdingId = await SeedPendingSyncAsync();

        await ConfirmAsync(holdingId, externalRef: "POL-00042");
        await ConfirmAsync(holdingId, externalRef: "POL-99999");

        (await ReloadAsync(holdingId)).ExternalRef.Should().Be("POL-00042");
    }

    // ── rejection ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refused_subscription_carries_a_reason_an_agent_can_act_on()
    {
        var holdingId = await SeedPendingSyncAsync();

        await RejectAsync(holdingId);

        var holding = await ReloadAsync(holdingId);
        holding.Status.Should().Be(HoldingStatus.Rejected);
        holding.RejectionReason.Should().StartWith("Functional/BENEFICIARY_UNDERAGE",
            "the family says whether the institution has something to fix or the insurer refused "
            + "on the merits, and an agent must not have to open the integration console to tell");
        holding.RejectionReason.Should().Contain("mineur");
    }

    [Fact]
    public async Task A_rejection_with_no_detail_still_names_the_family_and_the_code()
    {
        var holdingId = await SeedPendingSyncAsync();

        await RejectAsync(holdingId, detail: null);

        (await ReloadAsync(holdingId)).RejectionReason.Should().Be("Functional/BENEFICIARY_UNDERAGE");
    }

    [Fact]
    public async Task A_very_long_detail_is_truncated_rather_than_dropped()
    {
        // An adapter's message can be a stack trace or a whole SOAP fault, and the column holds
        // 1000 characters — but the first line of a refusal is usually the part that says why.
        var holdingId = await SeedPendingSyncAsync();

        await RejectAsync(holdingId, detail: new string('x', 5_000));

        var reason = (await ReloadAsync(holdingId)).RejectionReason;
        reason.Should().NotBeNull();
        reason!.Length.Should().BeLessThan(1_000);
        reason.Should().StartWith("Functional/BENEFICIARY_UNDERAGE");
        reason.Should().EndWith("…");
    }

    [Fact]
    public async Task A_rejected_holding_can_be_corrected_and_sent_again()
    {
        var holdingId = await SeedPendingSyncAsync();
        await RejectAsync(holdingId);

        await using var db = _factory.CreateContext();
        var holding = await db.ProductHoldings
            .AsTracking()
            .Include(h => h.Designations)
            .FirstAsync(h => h.Id == holdingId, CancellationToken.None);

        holding.ReturnToDraft(DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();
        holding.SignedAt.Should().BeNull("the corrected terms are not what the client signed");
    }

    [Fact]
    public async Task A_redelivered_rejection_does_not_overwrite_the_first_reason()
    {
        // The first reason is the one the agent has been looking at.
        var holdingId = await SeedPendingSyncAsync();

        await RejectAsync(holdingId, code: "BENEFICIARY_UNDERAGE");
        await RejectAsync(holdingId, code: "SOMETHING_ELSE");

        (await ReloadAsync(holdingId)).RejectionReason
            .Should().StartWith("Functional/BENEFICIARY_UNDERAGE");
    }

    // ── what the consumers must NOT touch ───────────────────────────────────

    [Fact]
    public async Task A_command_about_anything_but_a_policy_is_ignored_before_the_inbox()
    {
        // Claiming an inbox row for every successful command of every entity type would fill the
        // table with rows these consumers have nothing to do with.
        var holdingId = await SeedPendingSyncAsync();

        await ConfirmAsync(holdingId, entityType: "Customer");

        (await ReloadAsync(holdingId)).Status.Should().Be(HoldingStatus.PendingSync);
        _inbox.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_CRM_id_that_is_not_a_holding_is_not_an_error()
    {
        // Expected for every subscription keyed by customer rather than by holding — which is
        // every one issued before BNF-B-16.
        var act = async () => await ConfirmAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_holding_of_ANOTHER_tenant_is_not_found()
    {
        var holdingId = await SeedPendingSyncAsync();

        await using var db = _factory.CreateContext();
        var consumer = new HoldingSyncConfirmedConsumer(
            // A service bound to the right DbContext, but the event names a different tenant.
            Sync(db), _inbox, NullLogger<HoldingSyncConfirmedConsumer>.Instance);

        await consumer.Consume(Delivered(new IntegrationCommandSucceededEvent(
            TenantId: Guid.NewGuid(),
            CommandId: Guid.NewGuid(),
            ConnectionId: Guid.NewGuid(),
            CommandType: "Orass",
            EntityType: "Policy",
            CrmId: holdingId,
            ExternalRef: "POL-00042",
            SucceededAt: DateTimeOffset.UtcNow), Guid.NewGuid()));

        (await ReloadAsync(holdingId)).Status.Should().Be(HoldingStatus.PendingSync,
            "the service queries IgnoreQueryFilters WITH an explicit tenant predicate");
    }
}
