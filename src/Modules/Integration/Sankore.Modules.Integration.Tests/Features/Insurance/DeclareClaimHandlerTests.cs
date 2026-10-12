namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.Claims;
using Sankore.Modules.Integration.Features.Insurance.Claims.DeclareClaim;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;
using static Sankore.Modules.Integration.Tests.Features.Insurance.InsuranceTestHarness;

/// <summary>
/// ASS-09, first and second criteria: an agent declares a loss, and the dossier plus the queued
/// declaration are written together or not at all.
///
/// <para>
/// The case that drove the design is <see cref="A_loss_during_a_cover_that_has_since_ENDED_is_declarable"/>.
/// Gating on the policy's current status — the obvious implementation — denies a customer the
/// cover they had paid for on the day of the fire, and it is the kind of refusal that only
/// surfaces at a counter with the customer standing there.
/// </para>
/// </summary>
public sealed class DeclareClaimHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateOnly Loss = new(2026, 4, 10);

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly Guid _commandId = Guid.NewGuid();

    private InsuranceClaimPayload? _sent;

    public DeclareClaimHandlerTests()
        => _integration.Insurance
            .RequestClaimDeclarationAsync(
                Arg.Do<InsuranceClaimPayload>(p => _sent = p), Arg.Any<CancellationToken>())
            .Returns(_ => new IntegrationCommandId(_commandId));

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private async Task<PolicyRecord> SeedPolicyAsync(
        PolicyStatus status = PolicyStatus.Issued,
        DateOnly? effective = null,
        DateOnly? expiry = null)
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
            status: status,
            effectiveDate: effective ?? new DateOnly(2026, 1, 1),
            premiumAmount: 7_500m,
            currency: "XOF",
            periodicity: PremiumPeriodicity.Annual,
            clock: Clock(),
            expiryDate: expiry);

        db.Policies.Add(policy);
        await db.SaveChangesAsync();

        return policy;
    }

    private async Task<Result<DeclareClaimResult>> DeclareAsync(
        Guid policyId,
        DateOnly? occurredOn = null,
        string nature = "Incendie",
        string? description = "Sinistre incendie au domicile de l'assuré.")
    {
        await using var db = _factory.CreateContext();

        return await new DeclareClaimHandler(
                db,
                _integration,
                Features.Commands.CommandsTestHarness.Encryptor(),
                User(Tenant),
                Clock(),
                NullLogger<DeclareClaimHandler>.Instance)
            .Handle(
                new DeclareClaimCommand(policyId, occurredOn ?? Loss, nature, description),
                CancellationToken.None);
    }

    private async Task<List<ClaimRecord>> ClaimsAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.Claims.IgnoreQueryFilters().ToListAsync();
    }

    // ── the happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_loss_on_a_live_policy_opens_a_dossier_and_queues_the_declaration()
    {
        var policy = await SeedPolicyAsync();

        var result = await DeclareAsync(policy.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AlreadyExisted.Should().BeFalse();
        result.Value.DeclareCommandId.Should().Be(_commandId);
        result.Value.Status.Should().Be(nameof(ClaimStatus.Declared));

        var claim = (await ClaimsAsync()).Should().ContainSingle().Subject;
        claim.PolicyId.Should().Be(policy.Id);
        claim.CrmCustomerId.Should().Be(Customer);
        claim.ConnectionId.Should().Be(policy.ConnectionId);
        claim.OccurredOn.Should().Be(Loss);
        claim.Nature.Should().Be("Incendie");

        // The command id is on the dossier, which is what the acknowledgement consumer finds it
        // by. A dossier with none is one the insurer's answer can never be matched to.
        claim.DeclareCommandId.Should().Be(_commandId);
    }

    [Fact]
    public async Task The_account_of_the_loss_is_encrypted_in_the_column()
    {
        var policy = await SeedPolicyAsync();

        await DeclareAsync(policy.Id, description: "Décès de l'assuré le 10 avril.");

        var claim = (await ClaimsAsync()).Single();

        // The module's own AES-256-GCM primitive, not a passthrough double, so the assertion can
        // be the one that matters: the clear account of the loss is nowhere in the column.
        claim.DescriptionEncrypted.Should().StartWith("v1:");
        claim.DescriptionEncrypted.Should().NotContain("Décès");
        claim.DescriptionEncrypted.Should().NotContain("assuré");
    }

    [Fact]
    public async Task An_absent_account_stays_null_rather_than_becoming_a_decryptable_blank()
    {
        var policy = await SeedPolicyAsync();

        await DeclareAsync(policy.Id, description: null);

        (await ClaimsAsync()).Single().DescriptionEncrypted.Should().BeNull();
    }

    [Fact]
    public async Task The_insurer_is_sent_the_policy_s_OWN_external_id_and_no_documents()
    {
        var policy = await SeedPolicyAsync();

        await DeclareAsync(policy.Id);

        _sent.Should().NotBeNull();
        _sent!.PolicyId.Value.Should().Be("POL-CI-0001");
        _sent.OccurredOn.Should().Be(Loss);

        // Nothing has been scanned at the moment a declaration is made, and a pièce de sinistre
        // does not leave this platform before the antivirus pass has run on it.
        _sent.DocumentStorageRefs.Should().BeEmpty();
    }

    // ── the cover window ────────────────────────────────────────────────────

    [Fact]
    public async Task A_loss_during_a_cover_that_has_since_ENDED_is_declarable()
    {
        // The case that drove the design. A contract cancelled since covered the fire that
        // happened while it was in force; refusing because the policy reads Cancelled today
        // would deny a customer a cover they had paid for.
        var policy = await SeedPolicyAsync(
            status: PolicyStatus.Cancelled,
            effective: new DateOnly(2026, 1, 1),
            expiry: new DateOnly(2026, 5, 1));

        var result = await DeclareAsync(policy.Id, occurredOn: new DateOnly(2026, 4, 10));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_loss_BEFORE_the_cover_started_is_refused_and_says_which_end()
    {
        var policy = await SeedPolicyAsync(effective: new DateOnly(2026, 4, 1));

        var result = await DeclareAsync(policy.Id, occurredOn: new DateOnly(2026, 3, 31));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"{ClaimErrors.LossOutsideCover}:BEFORE_COVER");
    }

    [Fact]
    public async Task A_loss_AFTER_the_cover_ended_is_refused_and_says_which_end()
    {
        var policy = await SeedPolicyAsync(expiry: new DateOnly(2026, 3, 31));

        var result = await DeclareAsync(policy.Id, occurredOn: new DateOnly(2026, 4, 10));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"{ClaimErrors.LossOutsideCover}:AFTER_COVER");
    }

    [Fact]
    public async Task A_policy_with_no_expiry_covers_a_loss_of_today()
    {
        // An open-ended contract — a life policy with no term — must not be read as "expired".
        var policy = await SeedPolicyAsync(expiry: null);

        var result = await DeclareAsync(policy.Id, occurredOn: Today);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_loss_dated_in_the_future_is_refused()
    {
        var policy = await SeedPolicyAsync();

        var result = await DeclareAsync(policy.Id, occurredOn: Today.AddDays(1));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ClaimErrors.LossInTheFuture);
    }

    // ── idempotence ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Declaring_the_same_loss_twice_opens_ONE_dossier()
    {
        // Asserted on the row COUNT, because EF InMemory honours no unique index and
        // ux_ins_claim_declaration is what makes this true under concurrency.
        var policy = await SeedPolicyAsync();

        await DeclareAsync(policy.Id);
        var second = await DeclareAsync(policy.Id);

        second.IsSuccess.Should().BeTrue("the agent asked for the loss to be declared, and it is");
        second.Value!.AlreadyExisted.Should().BeTrue();

        (await ClaimsAsync()).Should().HaveCount(1);

        // And no second command: a double-click must not declare the loss twice at the insurer.
        await _integration.Insurance.Received(1).RequestClaimDeclarationAsync(
            Arg.Any<InsuranceClaimPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_DIFFERENT_nature_on_the_same_day_is_a_second_dossier()
    {
        // Rare and real: a fire and a theft on one policy on one day. The nature is the
        // discriminator in both the index and the command's idempotency key.
        var policy = await SeedPolicyAsync();

        await DeclareAsync(policy.Id, nature: "Incendie");
        var second = await DeclareAsync(policy.Id, nature: "Vol");

        second.Value!.AlreadyExisted.Should().BeFalse();
        (await ClaimsAsync()).Should().HaveCount(2);
    }

    // ── refusals ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_policy_is_NOT_FOUND_not_forbidden()
    {
        // Another tenant's policy is simply absent through the query filter, so the same code
        // answers both cases — a policy's existence must not leak.
        var result = await DeclareAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ClaimErrors.PolicyNotFound);
    }

    [Fact]
    public async Task An_insurer_that_refuses_the_command_leaves_NO_dossier_behind()
    {
        // A dossier with no queued command is a claim the insurer never hears about while the
        // customer has been told it is in. Nothing is saved, so nothing is left for a later run
        // to find — and nothing would.
        var policy = await SeedPolicyAsync();

        _integration.Insurance
            .RequestClaimDeclarationAsync(
                Arg.Any<InsuranceClaimPayload>(), Arg.Any<CancellationToken>())
            .Returns<IntegrationCommandId>(_ => throw new DomainException(
                $"{IntegrationErrors.CapabilityNotSupported}: this insurer takes no declarations."));

        var result = await DeclareAsync(policy.Id);

        result.IsFailure.Should().BeTrue();
        (await ClaimsAsync()).Should().BeEmpty();
    }
}
