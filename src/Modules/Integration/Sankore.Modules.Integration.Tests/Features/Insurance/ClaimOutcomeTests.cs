namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.Claims;
using Sankore.Modules.Integration.Features.Insurance.Claims.RecordIndemnity;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;
using static Sankore.Modules.Integration.Tests.Features.Insurance.InsuranceTestHarness;

/// <summary>
/// ASS-09's two ends: the insurer's acknowledgement of the declaration (criterion 2) and the
/// indemnity credited to the customer (criterion 5).
/// </summary>
public sealed class ClaimOutcomeTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Loss = new(2026, 4, 10);

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private async Task<(Guid ClaimId, Guid CommandId)> SeedClaimAsync(
        ClaimStatus status = ClaimStatus.Declared,
        string? externalClaimId = null)
    {
        await using var db = _factory.CreateContext();

        var connection = SeedConnection(db, Tenant, IntegrationKind.Fake, name: "ORASS Vie");

        var policy = PolicyRecord.Create(
            tenantId: Tenant,
            connectionId: connection.Id,
            crmCustomerId: Customer,
            externalPolicyId: "POL-CI-0001",
            policyNumber: "POL-CI-0001",
            insurerProductCode: "ASS-VIE-EMP",
            status: PolicyStatus.Issued,
            effectiveDate: new DateOnly(2026, 1, 1),
            premiumAmount: 7_500m,
            currency: "XOF",
            periodicity: PremiumPeriodicity.Annual,
            clock: Clock(Now));

        var claim = ClaimRecord.Create(
            tenantId: Tenant,
            connectionId: connection.Id,
            policyId: policy.Id,
            crmCustomerId: Customer,
            occurredOn: Loss,
            nature: "Incendie",
            declaredBy: Actor,
            clock: Clock(Now));

        var commandId = Guid.NewGuid();
        claim.AttachDeclareCommand(commandId, Clock(Now));

        if (externalClaimId is not null)
            claim.RecordInsurerReference(externalClaimId, externalClaimId, Clock(Now));

        if (status != ClaimStatus.Declared)
            claim.ApplySync(status, null, null, Clock(Now));

        db.Policies.Add(policy);
        db.Claims.Add(claim);
        await db.SaveChangesAsync();

        return (claim.Id, commandId);
    }

    private async Task<ClaimRecord> ClaimAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.Claims.IgnoreQueryFilters().FirstAsync(c => c.Id == id);
    }

    // ── criterion 2: the acknowledgement ────────────────────────────────────

    private async Task AcknowledgeAsync(
        Guid commandId, string? externalRef, string commandType = nameof(CommandType.DeclareClaim))
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock(Now));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IIntegrationInboxGuard>(new GrantingInbox());
        services.AddScoped(_ => _factory.CreateContext());

        await using var provider = services.BuildServiceProvider();

        var context = Substitute.For<ConsumeContext<IntegrationCommandSucceededEvent>>();
        context.Message.Returns(new IntegrationCommandSucceededEvent(
            Tenant, commandId, Guid.NewGuid(), commandType,
            IntegrationEntityTypes.Claim, Customer, externalRef, Now));
        context.MessageId.Returns(Guid.NewGuid());
        context.CancellationToken.Returns(CancellationToken.None);

        await new ClaimDeclarationAcknowledgedConsumer(
                new ClaimScopeFactory(provider),
                NullLogger<ClaimDeclarationAcknowledgedConsumer>.Instance)
            .Consume(context);
    }

    [Fact]
    public async Task The_insurer_s_dossier_reference_is_recorded_on_the_claim()
    {
        var (claimId, commandId) = await SeedClaimAsync();

        await AcknowledgeAsync(commandId, "CLM-CI-0042");

        (await ClaimAsync(claimId)).ExternalClaimId.Should().Be("CLM-CI-0042");
    }

    [Fact]
    public async Task A_success_with_NO_reference_leaves_the_dossier_unacknowledged()
    {
        // The reference IS what the consumer exists to store, and a dossier carrying a blank one
        // is irreconcilable — the same rule a holding's ExternalRef follows.
        var (claimId, commandId) = await SeedClaimAsync();

        await AcknowledgeAsync(commandId, externalRef: null);

        (await ClaimAsync(claimId)).ExternalClaimId.Should().BeNull();
    }

    [Fact]
    public async Task A_DIFFERENT_reference_does_not_re_point_the_dossier()
    {
        // Two insurer references for one dossier means one system is wrong, and picking a side
        // silently is what a reconciliation cannot undo. Caught and logged rather than left to
        // kill the consumer on every redelivery.
        var (claimId, commandId) = await SeedClaimAsync(externalClaimId: "CLM-CI-0001");

        var act = async () => await AcknowledgeAsync(commandId, "CLM-CI-9999");

        await act.Should().NotThrowAsync();

        (await ClaimAsync(claimId)).ExternalClaimId.Should().Be("CLM-CI-0001");
    }

    [Fact]
    public async Task The_SAME_reference_twice_changes_nothing()
    {
        var (claimId, commandId) = await SeedClaimAsync();

        await AcknowledgeAsync(commandId, "CLM-CI-0042");
        await AcknowledgeAsync(commandId, "CLM-CI-0042");

        (await ClaimAsync(claimId)).ExternalClaimId.Should().Be("CLM-CI-0042");
    }

    [Fact]
    public async Task Another_command_type_is_left_alone()
    {
        // Every succeeded command publishes the same event. A consumer that answered to the
        // entity type alone would half-handle a document transmission's success too.
        var (claimId, commandId) = await SeedClaimAsync();

        await AcknowledgeAsync(
            commandId, "POL-CI-0001", commandType: nameof(CommandType.SubscribePolicy));

        (await ClaimAsync(claimId)).ExternalClaimId.Should().BeNull();
    }

    // ── criterion 5: the indemnity ──────────────────────────────────────────

    private async Task<Result<ClaimDto>> RecordIndemnityAsync(
        Guid claimId, decimal amount = 1_250_000m, string currency = "XOF",
        string? reference = "CBS-CR-7700")
    {
        await using var db = _factory.CreateContext();

        return await new RecordClaimIndemnityHandler(
                db, Clock(Now), NullLogger<RecordClaimIndemnityHandler>.Instance)
            .Handle(
                new RecordClaimIndemnityCommand(claimId, amount, currency, reference),
                CancellationToken.None);
    }

    [Fact]
    public async Task The_credit_s_reference_lands_on_the_dossier_and_settles_it()
    {
        var (claimId, _) = await SeedClaimAsync(status: ClaimStatus.Accepted);

        var result = await RecordIndemnityAsync(claimId);

        result.IsSuccess.Should().BeTrue();

        var claim = await ClaimAsync(claimId);
        claim.IndemnityAmount.Should().Be(1_250_000m);
        claim.IndemnityCurrency.Should().Be("XOF");
        claim.IndemnityCbsReference.Should().Be("CBS-CR-7700");
        claim.IndemnityPaidAt.Should().Be(Now);

        // The status follows the money: the customer has been paid, whatever the last sync said.
        claim.Status.Should().Be(ClaimStatus.Indemnified);
        claim.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Submitting_the_SAME_credit_reference_twice_changes_nothing()
    {
        // Two operators reconciling one credit, or one clicking twice.
        var (claimId, _) = await SeedClaimAsync(status: ClaimStatus.Accepted);

        await RecordIndemnityAsync(claimId);
        var second = await RecordIndemnityAsync(claimId);

        second.IsSuccess.Should().BeTrue();
        (await ClaimAsync(claimId)).IndemnityPaidAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_DIFFERENT_credit_reference_on_a_paid_dossier_is_a_conflict()
    {
        var (claimId, _) = await SeedClaimAsync(status: ClaimStatus.Accepted);

        await RecordIndemnityAsync(claimId, reference: "CBS-CR-7700");
        var second = await RecordIndemnityAsync(claimId, reference: "CBS-CR-8800");

        second.IsFailure.Should().BeTrue();
        second.Error.Should().Be($"{ClaimErrors.IndemnityConflict}:REFERENCE_ALREADY_RECORDED");

        (await ClaimAsync(claimId)).IndemnityCbsReference.Should().Be("CBS-CR-7700");
    }

    [Fact]
    public async Task An_indemnity_on_a_dossier_the_insurer_REFUSED_is_a_conflict()
    {
        // Two systems disagree about money. Whichever side is wrong, the correction is a human
        // decision, and recording the payment would hide the contradiction.
        var (claimId, _) = await SeedClaimAsync(status: ClaimStatus.Refused);

        var result = await RecordIndemnityAsync(claimId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"{ClaimErrors.IndemnityConflict}:CLAIM_REFUSED");

        (await ClaimAsync(claimId)).IndemnityPaidAt.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_dossier_is_NOT_FOUND()
    {
        var result = await RecordIndemnityAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ClaimErrors.ClaimNotFound);
    }

    [Fact]
    public async Task A_non_positive_amount_is_refused_by_the_aggregate_too()
    {
        // The validator refuses it first, with a 400 naming the field. This pins the aggregate's
        // own guard, which is what protects the path a consumer or a job would take.
        var (claimId, _) = await SeedClaimAsync(status: ClaimStatus.Accepted);

        var result = await RecordIndemnityAsync(claimId, amount: 0m);

        result.IsFailure.Should().BeTrue();
        (await ClaimAsync(claimId)).IndemnityPaidAt.Should().BeNull();
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private sealed class GrantingInbox : IIntegrationInboxGuard
    {
        private readonly HashSet<(Guid, string)> _claimed = [];

        public Task<bool> TryBeginAsync(
            Guid messageId, Guid tenantId, string eventType, string consumerKey,
            CancellationToken ct)
            => Task.FromResult(_claimed.Add((messageId, consumerKey)));
    }

    private sealed class ClaimScopeFactory(IServiceProvider sp) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(sp);

        private sealed class Scope(IServiceProvider sp) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = sp;
            public void Dispose() { }
        }
    }
}
