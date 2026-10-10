namespace Sankore.Modules.Integration.Tests.Features.Onboarding;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Onboarding;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Xunit;

/// <summary>
/// INT-14 end to end, against the in-memory core banking double: an approved KYC file becomes a
/// customer in the CBS, then its tier, then its account — and every refusal on the way becomes
/// exactly one administrator alert.
///
/// <para>
/// The chain is driven step by step rather than through a bus harness because the steps are
/// joined by the OUTBOX, not by the broker: each consumer commits its command and its events in
/// one save, and the next step reads the event back out of the row the previous one wrote. Doing
/// it this way is what lets a test assert that the event was committed at all — an in-memory bus
/// would deliver an event a rolled-back transaction never persisted.
/// </para>
/// </summary>
public sealed class OnboardingChainTests
{
    private static readonly Guid MessageId = new("dddddddd-0000-0000-0000-000000000001");

    // ── Criterion 1 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Kyc_validation_queues_a_core_banking_customer_creation()
    {
        using var context = OnboardingTestContext.WithActiveConnection();

        await context.ConsumeKycValidatedAsync(MessageId);

        var commands = await context.CommandsAsync();

        commands.Should().ContainSingle()
            .Which.Should().Match<IntegrationCommand>(c =>
                c.CommandType == CommandType.CreateCustomer
                && c.EntityType == IntegrationEntityTypes.Customer
                && c.CrmId == context.CrmCustomerId
                && c.Status == CommandStatus.Pending);
    }

    [Fact]
    public async Task A_tenant_with_no_core_banking_connection_queues_nothing_and_does_not_throw()
    {
        using var context = OnboardingTestContext.WithoutConnection();

        await context.ConsumeKycValidatedAsync(MessageId);

        (await context.CommandsAsync()).Should().BeEmpty();

        // The message is still claimed: the guard commits before the attempt, so the event is
        // acknowledged. That is the behaviour, not an accident — see the consumer's comment on
        // why rethrowing would have the broker redeliver for ever.
        context.Inbox.WasClaimedBy(MessageId, KycValidatedOnboardingConsumer.ConsumerKey)
            .Should().BeTrue();
    }

    [Fact]
    public async Task The_system_placeholder_tenant_is_ignored_entirely()
    {
        using var context = OnboardingTestContext.WithActiveConnection();

        await context.ConsumeKycValidatedAsync(MessageId, tenantId: Guid.Empty);

        using var db = context.NewDb();

        // Not one row anywhere, for any tenant, and the message is never even claimed: the
        // consumer returns first, because Guid.Empty is the SYSTEM placeholder and names no
        // tenant to onboard.
        (await db.Commands.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        context.Inbox.Claims.Should().Be(0);
    }

    // ── Criteria 1 → 3, the whole chain ─────────────────────────────────────

    [Fact]
    public async Task The_whole_chain_creates_the_customer_sets_its_tier_and_opens_the_account()
    {
        using var context = OnboardingTestContext.WithActiveConnection(
            productCode: FakeAdapter.SeededProductCode);

        // 1. KYC validated → CreateCustomer queued, then dispatched.
        await context.ConsumeKycValidatedAsync(MessageId);
        await context.ExecutePendingCommandsAsync();

        var created = await context.PublishedAsync<CbsCustomerCreatedEvent>();

        created.Should().ContainSingle(
            "the success of the creation is what the rest of the chain hangs off (criterion 2)");

        created[0].CrmCustomerId.Should().Be(context.CrmCustomerId);
        created[0].ConnectionId.Should().Be(context.ConnectionId);
        created[0].ExternalCustomerId.Should().NotBeNullOrWhiteSpace();

        // 2. The chain reacts: KYC level, then the account because a product was chosen.
        await context.ConsumeCbsCustomerCreatedAsync(created[0], Guid.NewGuid());

        var queued = await context.CommandsAsync();

        queued.Select(c => c.CommandType).Should().BeEquivalentTo(
            [CommandType.CreateCustomer, CommandType.SetKycLevel, CommandType.OpenAccount]);

        await context.ExecutePendingCommandsAsync();

        // 3. Both writes reached the far end with the values they were meant to carry — asserted
        // on the double rather than on the encrypted payload column, which is the point of the
        // chain: what the CBS ended up holding.
        var externalCustomerId = created[0].ExternalCustomerId;

        context.Adapter.KycLevels.Should().ContainKey(externalCustomerId)
            .WhoseValue.Should().Be(KycLevel.Full);

        context.Adapter.AccountsByCustomer[externalCustomerId].Should().ContainSingle()
            .Which.ProductCode.Should().Be(FakeAdapter.SeededProductCode);

        // Criterion 3: the opening publishes the CBS references. Already published by
        // ExecuteIntegrationCommandHandler — this asserts they TRAVEL, and that both of them do:
        // an account reference without the customer reference cannot be shown on one screen.
        var opened = await context.PublishedAsync<CbsAccountOpenedEvent>();

        opened.Should().ContainSingle();
        opened[0].CrmCustomerId.Should().Be(context.CrmCustomerId);
        opened[0].ConnectionId.Should().Be(context.ConnectionId);
        opened[0].ExternalCustomerId.Should().Be(externalCustomerId);
        opened[0].ProductCode.Should().Be(FakeAdapter.SeededProductCode);
        opened[0].ExternalAccountId.Should()
            .Be(context.Adapter.AccountsByCustomer[externalCustomerId][0].AccountId.Value);

        // And INT-07's half of it: both references are addressable afterwards, which is what
        // stops the next command creating a second customer.
        using var db = context.NewDb();

        var references = await db.References
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == context.TenantId)
            .ToListAsync();

        references.Should().HaveCount(2);
        references.Should().Contain(r =>
            r.EntityType == IntegrationEntityTypes.Customer && r.ExternalId == externalCustomerId);
        references.Should().Contain(r =>
            r.EntityType == IntegrationEntityTypes.Account
            && r.ExternalId == opened[0].ExternalAccountId);

        // Nothing was rejected anywhere in a chain that succeeded.
        (await context.PublishedAsync<IntegrationCommandRejectedEvent>()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_chain_stops_after_the_kyc_level_when_no_product_was_chosen()
    {
        // The default, and the platform's real answer today: no contract exposes a product chosen
        // at enrolment, so IOnboardingProductSelector answers null. See its documentation.
        using var context = OnboardingTestContext.WithActiveConnection();

        await context.ConsumeKycValidatedAsync(MessageId);
        await context.ExecutePendingCommandsAsync();

        var created = await context.PublishedAsync<CbsCustomerCreatedEvent>();
        await context.ConsumeCbsCustomerCreatedAsync(created.Single(), Guid.NewGuid());

        var commands = await context.CommandsAsync();

        commands.Select(c => c.CommandType).Should().BeEquivalentTo(
            [CommandType.CreateCustomer, CommandType.SetKycLevel],
            "an account must never be opened on a product code nobody chose");

        await context.ExecutePendingCommandsAsync();

        // Stops CLEANLY: the tier is pushed, no account is opened, and nothing is reported as a
        // failure — there is nothing for an administrator to act on.
        context.Adapter.AccountsByCustomer.Should()
            .NotContainKey(created[0].ExternalCustomerId);

        (await context.PublishedAsync<CbsAccountOpenedEvent>()).Should().BeEmpty();
        (await context.PublishedAsync<IntegrationCommandRejectedEvent>()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_tier_pushed_is_the_one_module_two_computes()
    {
        // A simplified file must not be announced to the CBS as a full one: the ceilings the CBS
        // applies are the ones the tier buys. The tier is read through the module's single
        // definition (CbsCustomerPayloadSource), not re-derived in the consumer.
        using var context = OnboardingTestContext.WithActiveConnection(kycTier: "Simplified");

        await context.ConsumeKycValidatedAsync(MessageId);
        await context.ExecutePendingCommandsAsync();

        var created = await context.PublishedAsync<CbsCustomerCreatedEvent>();
        await context.ConsumeCbsCustomerCreatedAsync(created.Single(), Guid.NewGuid());
        await context.ExecutePendingCommandsAsync();

        context.Adapter.KycLevels[created[0].ExternalCustomerId].Should().Be(KycLevel.Simplified);
    }

    // ── Idempotency, and sharing the event with M01 ──────────────────────────

    [Fact]
    public async Task A_replayed_kyc_validation_queues_exactly_one_creation()
    {
        using var context = OnboardingTestContext.WithActiveConnection();

        await context.ConsumeKycValidatedAsync(MessageId);
        await context.ConsumeKycValidatedAsync(MessageId);
        await context.ConsumeKycValidatedAsync(MessageId);

        (await context.CommandsAsync()).Should().ContainSingle(
            "delivery is at-least-once, and the second claim loses on the inbox primary key");

        // Three deliveries reached the guard, one was granted: the consumer does not short-circuit
        // around it, and does not do the work when it is refused.
        context.Inbox.Claims.Should().Be(3);
    }

    [Fact]
    public async Task Another_consumer_of_the_same_message_is_not_starved_by_this_one()
    {
        using var context = OnboardingTestContext.WithActiveConnection();

        await context.ConsumeKycValidatedAsync(MessageId);

        // Both effects, which is the point: this module queued its creation AND a second consumer
        // of the SAME message still gets to claim it. Before the id was derived per consumer, one
        // of the two was silently skipped depending on which ran first.
        (await context.CommandsAsync()).Should().ContainSingle();

        context.Inbox.WasClaimedBy(MessageId, KycValidatedOnboardingConsumer.ConsumerKey)
            .Should().BeTrue();

        (await context.Inbox.TryBeginAsync(
                MessageId, context.TenantId, nameof(KycValidatedEvent),
                "AnotherConsumerOfKycValidated", CancellationToken.None))
            .Should().BeTrue("a second consumer of the same message must still get to claim it");

        // …while this consumer's OWN replay is still refused. The derivation buys coexistence,
        // not a weaker guard.
        (await context.Inbox.TryBeginAsync(
                MessageId, context.TenantId, nameof(KycValidatedEvent),
                KycValidatedOnboardingConsumer.ConsumerKey, CancellationToken.None))
            .Should().BeFalse();
    }

    // ── Criterion 4: one rejection, at each of the three steps ──────────────

    [Fact]
    public async Task A_refused_creation_publishes_exactly_one_rejection_and_stops_the_chain()
    {
        using var context = OnboardingTestContext.WithActiveConnection(
            productCode: FakeAdapter.SeededProductCode);

        // Functional and not transient: the far end answered and said no, so the command is
        // rejected on the first attempt instead of being retried eight times.
        context.Adapter.ForcedWriteOutcome = IntegrationResult.Functional(
            IntegrationErrors.Duplicate, "The external system already holds this record.");

        await context.ConsumeKycValidatedAsync(MessageId);
        await context.ExecuteNextAsync(CommandType.CreateCustomer);

        var rejections = await context.PublishedAsync<IntegrationCommandRejectedEvent>();

        rejections.Should().ContainSingle(
            "ExecuteIntegrationCommandHandler is the single publisher — a second one in the "
            + "onboarding chain would double-alert the administrator");

        rejections[0].CommandType.Should().Be(nameof(CommandType.CreateCustomer));
        rejections[0].CrmId.Should().Be(context.CrmCustomerId);
        rejections[0].ErrorFamily.Should().Be(nameof(ErrorFamily.Functional));
        rejections[0].ErrorCode.Should().Be(IntegrationErrors.Duplicate);

        // Nothing chains off a rejection: no creation event, so no tier and no account.
        (await context.PublishedAsync<CbsCustomerCreatedEvent>()).Should().BeEmpty();
        (await context.CommandsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_refused_kyc_level_publishes_exactly_one_rejection()
    {
        using var context = OnboardingTestContext.WithActiveConnection();

        await context.ConsumeKycValidatedAsync(MessageId);
        await context.ExecuteNextAsync(CommandType.CreateCustomer);

        var created = await context.PublishedAsync<CbsCustomerCreatedEvent>();
        await context.ConsumeCbsCustomerCreatedAsync(created.Single(), Guid.NewGuid());

        context.Adapter.ForcedWriteOutcome = IntegrationResult.Functional(
            IntegrationErrors.Rejected, "The external system refused the tier change.");

        await context.ExecuteNextAsync(CommandType.SetKycLevel);

        var rejections = await context.PublishedAsync<IntegrationCommandRejectedEvent>();

        rejections.Should().ContainSingle();
        rejections[0].CommandType.Should().Be(nameof(CommandType.SetKycLevel));
        rejections[0].ErrorCode.Should().Be(IntegrationErrors.Rejected);
    }

    [Fact]
    public async Task A_refused_account_opening_publishes_exactly_one_rejection()
    {
        using var context = OnboardingTestContext.WithActiveConnection(
            productCode: FakeAdapter.SeededProductCode);

        await context.ConsumeKycValidatedAsync(MessageId);
        await context.ExecuteNextAsync(CommandType.CreateCustomer);

        var created = await context.PublishedAsync<CbsCustomerCreatedEvent>();
        await context.ConsumeCbsCustomerCreatedAsync(created.Single(), Guid.NewGuid());

        // The tier goes through; only the opening is refused — the step the criterion names.
        await context.ExecuteNextAsync(CommandType.SetKycLevel);

        context.Adapter.ForcedWriteOutcome = IntegrationResult.Functional(
            IntegrationErrors.UnknownProduct, "The external system does not know this product.");

        await context.ExecuteNextAsync(CommandType.OpenAccount);

        var rejections = await context.PublishedAsync<IntegrationCommandRejectedEvent>();

        rejections.Should().ContainSingle();
        rejections[0].CommandType.Should().Be(nameof(CommandType.OpenAccount));
        rejections[0].EntityType.Should().Be(IntegrationEntityTypes.Account);
        rejections[0].ErrorCode.Should().Be(IntegrationErrors.UnknownProduct);

        (await context.PublishedAsync<CbsAccountOpenedEvent>()).Should().BeEmpty();
    }

    // ── The chain's own idempotency at step two ─────────────────────────────

    [Fact]
    public async Task A_replayed_creation_event_queues_the_follow_up_commands_once()
    {
        using var context = OnboardingTestContext.WithActiveConnection(
            productCode: FakeAdapter.SeededProductCode);

        await context.ConsumeKycValidatedAsync(MessageId);
        await context.ExecuteNextAsync(CommandType.CreateCustomer);

        var created = (await context.PublishedAsync<CbsCustomerCreatedEvent>()).Single();
        var chainMessageId = Guid.NewGuid();

        await context.ConsumeCbsCustomerCreatedAsync(created, chainMessageId);
        await context.ConsumeCbsCustomerCreatedAsync(created, chainMessageId);

        (await context.CommandsAsync()).Select(c => c.CommandType).Should().BeEquivalentTo(
            [CommandType.CreateCustomer, CommandType.SetKycLevel, CommandType.OpenAccount]);
    }
}
