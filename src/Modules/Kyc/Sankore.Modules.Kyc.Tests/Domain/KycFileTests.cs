namespace Sankore.Modules.Kyc.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Kyc.Domain;
using Xunit;

/// <summary>
/// The transition table is a compliance rule, so it is tested as a rule and not through the
/// handlers that happen to call it: an invalid move must come back as a result an operator can be
/// shown, never as an exception.
/// </summary>
public sealed class KycFileTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();

    private static KycFile Open() =>
        KycFile.Open(Tenant, Customer, KycChannel.Agency, Agent, TimeProvider.System);

    /// <summary>Drives a file to <paramref name="target"/> through legal moves only.</summary>
    private static KycFile At(KycFileStatus target)
    {
        var file = Open();
        if (target == KycFileStatus.Collecting) return file;

        file.SubmitForVerification(Agent, TimeProvider.System).IsSuccess.Should().BeTrue();
        if (target == KycFileStatus.Verifying) return file;

        if (target == KycFileStatus.ComplementRequired)
        {
            file.RecordVerification(10, KycConfidenceLevel.Rejected, TimeProvider.System);
            return file;
        }

        file.RecordVerification(90, KycConfidenceLevel.High, TimeProvider.System);
        if (target == KycFileStatus.Validating) return file;

        if (target == KycFileStatus.Rejected)
        {
            file.Reject(Manager, TimeProvider.System);
            return file;
        }

        file.Approve(
            target == KycFileStatus.Simplified ? KycTier.Simplified : KycTier.Full,
            Manager, TimeProvider.System);

        if (target is KycFileStatus.Simplified or KycFileStatus.Full) return file;

        file.StartReview(TimeProvider.System);
        if (target == KycFileStatus.UnderReview) return file;

        if (target == KycFileStatus.Expired)
        {
            file.Expire(TimeProvider.System);
            return file;
        }

        file.Suspend(TimeProvider.System);
        return file;
    }

    // ── creation ────────────────────────────────────────────────────────────

    [Fact]
    public void A_new_file_starts_collecting_with_no_tier()
    {
        var file = Open();

        file.Status.Should().Be(KycFileStatus.Collecting);
        file.Tier.Should().Be(KycTier.None);
        file.VigilanceLevel.Should().Be(KycVigilanceLevel.Standard);
        file.ConfidenceScore.Should().BeNull();
        file.ValidatedAt.Should().BeNull();
        file.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void A_file_can_be_created_with_a_caller_supplied_id()
    {
        // So a command can publish an event referencing the file before SaveChanges.
        var id = Guid.NewGuid();

        KycFile.Open(Tenant, Customer, KycChannel.Web, Agent, TimeProvider.System, id: id)
            .Id.Should().Be(id);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_file_without_a_tenant_or_a_customer_is_refused(bool blankTenant, bool blankCustomer)
    {
        var act = () => KycFile.Open(
            blankTenant ? Guid.Empty : Tenant,
            blankCustomer ? Guid.Empty : Customer,
            KycChannel.Agency, Agent, TimeProvider.System);

        act.Should().Throw<Sankore.Shared.Kernel.DomainException>();
    }

    // ── the transition table ────────────────────────────────────────────────

    [Theory]
    [InlineData(KycFileStatus.Collecting, KycFileStatus.Verifying)]
    [InlineData(KycFileStatus.Verifying, KycFileStatus.Validating)]
    [InlineData(KycFileStatus.Verifying, KycFileStatus.ComplementRequired)]
    [InlineData(KycFileStatus.Validating, KycFileStatus.Full)]
    [InlineData(KycFileStatus.ComplementRequired, KycFileStatus.Verifying)]
    [InlineData(KycFileStatus.Full, KycFileStatus.UnderReview)]
    [InlineData(KycFileStatus.Full, KycFileStatus.Simplified)]
    [InlineData(KycFileStatus.UnderReview, KycFileStatus.Expired)]
    [InlineData(KycFileStatus.Expired, KycFileStatus.UnderReview)]
    public void An_allowed_move_is_allowed(KycFileStatus from, KycFileStatus to)
        => At(from).CanTransitionTo(to).Should().BeTrue();

    [Theory]
    [InlineData(KycFileStatus.Collecting, KycFileStatus.Full)]
    [InlineData(KycFileStatus.Collecting, KycFileStatus.Validating)]
    [InlineData(KycFileStatus.Verifying, KycFileStatus.Full)]
    [InlineData(KycFileStatus.Validating, KycFileStatus.Verifying)]
    [InlineData(KycFileStatus.Expired, KycFileStatus.Full)]
    public void A_move_that_skips_a_step_is_refused(KycFileStatus from, KycFileStatus to)
        => At(from).CanTransitionTo(to).Should().BeFalse();

    [Theory]
    [InlineData(KycFileStatus.Rejected)]
    [InlineData(KycFileStatus.Suspended)]
    public void A_terminal_status_accepts_nothing(KycFileStatus terminal)
    {
        var file = At(terminal);

        file.IsOpen.Should().BeFalse();
        foreach (var target in Enum.GetValues<KycFileStatus>())
            file.CanTransitionTo(target).Should().BeFalse($"{terminal} is final");
    }

    [Fact]
    public void An_invalid_move_is_a_result_not_an_exception()
    {
        // An agent must see a message, not a 500.
        var file = Open();

        var result = file.Approve(KycTier.Full, Manager, TimeProvider.System);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.InvalidTransition);
        file.Status.Should().Be(KycFileStatus.Collecting);
    }

    // ── verification routing ────────────────────────────────────────────────

    [Fact]
    public void A_rejected_score_sends_the_file_back_to_the_agent()
    {
        var file = At(KycFileStatus.Verifying);

        file.RecordVerification(15, KycConfidenceLevel.Rejected, TimeProvider.System)
            .IsSuccess.Should().BeTrue();

        file.Status.Should().Be(KycFileStatus.ComplementRequired);
        file.ConfidenceScore.Should().Be(15);
        file.ConfidenceLevel.Should().Be(KycConfidenceLevel.Rejected);
    }

    [Theory]
    [InlineData(KycConfidenceLevel.Low)]
    [InlineData(KycConfidenceLevel.Medium)]
    [InlineData(KycConfidenceLevel.High)]
    public void Any_other_score_enters_the_approval_circuit(KycConfidenceLevel level)
    {
        var file = At(KycFileStatus.Verifying);

        file.RecordVerification(70, level, TimeProvider.System);

        file.Status.Should().Be(KycFileStatus.Validating);
    }

    [Fact]
    public void A_score_outside_zero_to_one_hundred_is_clamped()
    {
        var file = At(KycFileStatus.Verifying);

        file.RecordVerification(150, KycConfidenceLevel.High, TimeProvider.System);

        file.ConfidenceScore.Should().Be(100);
    }

    [Fact]
    public void A_verification_can_only_be_recorded_while_verifying()
    {
        var file = Open();

        file.RecordVerification(90, KycConfidenceLevel.High, TimeProvider.System)
            .Error.Should().Be(KycErrors.InvalidTransition);
    }

    // ── four eyes ───────────────────────────────────────────────────────────

    [Fact]
    public void The_person_who_submitted_a_file_cannot_approve_it()
    {
        // Owned here, not by the workflow engine: M01 found on client merges that the engine runs
        // the circuit but enforces no self-approval rule of its own.
        var file = At(KycFileStatus.Validating);

        var result = file.Approve(KycTier.Full, Agent, TimeProvider.System);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.SelfApprovalForbidden);
        file.Status.Should().Be(KycFileStatus.Validating);
    }

    [Fact]
    public void Nor_reject_it()
    {
        var file = At(KycFileStatus.Validating);

        file.Reject(Agent, TimeProvider.System).Error.Should().Be(KycErrors.SelfApprovalForbidden);
    }

    [Fact]
    public void Approving_without_a_tier_is_refused()
    {
        At(KycFileStatus.Validating)
            .Approve(KycTier.None, Manager, TimeProvider.System)
            .IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(KycTier.Simplified, KycFileStatus.Simplified)]
    [InlineData(KycTier.Full, KycFileStatus.Full)]
    public void Approval_sets_the_tier_the_status_and_the_validation_date(
        KycTier tier, KycFileStatus expected)
    {
        var file = At(KycFileStatus.Validating);
        var due = new DateOnly(2029, 10, 1);

        file.Approve(tier, Manager, TimeProvider.System, nextReviewDate: due)
            .IsSuccess.Should().BeTrue();

        file.Status.Should().Be(expected);
        file.Tier.Should().Be(tier);
        file.ValidatedAt.Should().NotBeNull();
        file.NextReviewDate.Should().Be(due);
    }

    // ── review, expiry, downgrade ───────────────────────────────────────────

    [Fact]
    public void A_full_file_can_be_downgraded_to_simplified_without_being_rejected()
    {
        // An expired identity document caps the customer; it does not end the relationship.
        var file = At(KycFileStatus.Full);

        file.DowngradeToSimplified(TimeProvider.System).IsSuccess.Should().BeTrue();

        file.Status.Should().Be(KycFileStatus.Simplified);
        file.Tier.Should().Be(KycTier.Simplified);
        file.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void An_expired_file_can_only_go_back_to_review()
    {
        var file = At(KycFileStatus.Expired);

        file.CanTransitionTo(KycFileStatus.UnderReview).Should().BeTrue();
        file.CanTransitionTo(KycFileStatus.Full).Should().BeFalse();
        file.IsOpen.Should().BeTrue("an expired customer is capped, not cut off");
    }

    // ── flags ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_suspected_duplicate_raises_the_vigilance_level()
    {
        var file = Open();

        file.FlagDuplicateSuspected(TimeProvider.System);

        file.DuplicateSuspected.Should().BeTrue();
        file.VigilanceLevel.Should().Be(KycVigilanceLevel.High,
            "the circuit must gain the compliance officer");
    }

    [Fact]
    public void Clearing_the_flag_leaves_the_vigilance_level_where_compliance_set_it()
    {
        // Lifting a false positive is not a reason to forget the file was once suspect.
        var file = Open();
        file.FlagDuplicateSuspected(TimeProvider.System);

        file.ClearDuplicateSuspicion(TimeProvider.System);

        file.DuplicateSuspected.Should().BeFalse();
        file.VigilanceLevel.Should().Be(KycVigilanceLevel.High);
    }

    [Fact]
    public void Face_match_attempts_accumulate_on_the_file()
    {
        var file = Open();

        file.RecordFaceMatchAttempt(TimeProvider.System);
        file.RecordFaceMatchAttempt(TimeProvider.System);

        file.FaceMatchAttempts.Should().Be(2);
    }

    // ── status changes are observable ───────────────────────────────────────

    [Fact]
    public void Every_status_change_raises_a_domain_event()
    {
        var file = Open();
        file.ClearDomainEvents();

        file.SubmitForVerification(Agent, TimeProvider.System);

        file.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<KycFileStatusChangedDomainEvent>()
            .Which.Should().Match<KycFileStatusChangedDomainEvent>(e =>
                e.Previous == KycFileStatus.Collecting && e.Current == KycFileStatus.Verifying);
    }

    [Fact]
    public void A_refused_change_raises_nothing()
    {
        var file = Open();
        file.ClearDomainEvents();

        file.Approve(KycTier.Full, Manager, TimeProvider.System);

        file.DomainEvents.Should().BeEmpty();
    }
}
