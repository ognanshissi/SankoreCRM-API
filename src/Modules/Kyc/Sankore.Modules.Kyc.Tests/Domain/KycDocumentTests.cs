namespace Sankore.Modules.Kyc.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// KYC-F-02 — la décision d'un validateur sur une pièce est une preuve.
///
/// <para>
/// Decide-once is the rule worth pinning, for the same reason <c>KycApprovalStep</c> has it: a
/// document decision names who judged what, and re-deciding it would rewrite that. The way to
/// change the answer is a new upload, which is a new row — so the refusal that prompted it survives
/// as the reason the second image was needed.
/// </para>
/// </summary>
public sealed class KycDocumentTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid FileId = Guid.NewGuid();
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly Guid Validator = Guid.NewGuid();

    private const string Motive = "Photo floue, le numéro est illisible";

    private static KycDocument Register(
        KycDocumentKind kind = KycDocumentKind.IdentityDocumentFront) =>
        KycDocument.Register(
            Tenant, FileId, kind,
            storageRef: "k1.0123456789abcdef.0123456789abcdef0123456789abcdef",
            contentType: "image/jpeg", sizeBytes: 42, sha256: new string('a', 64),
            uploadedBy: Agent, clock: TimeProvider.System);

    // ── registration ────────────────────────────────────────────────────────

    [Fact]
    public void A_freshly_uploaded_document_is_pending_and_nothing_else()
    {
        var document = Register();

        document.ReviewDecision.Should().Be(KycDocumentReviewDecision.Pending);
        document.IsReviewed.Should().BeFalse();
        document.ReviewedBy.Should().BeNull();
        document.ReviewedAt.Should().BeNull();
        document.RefusalReason.Should().BeNull();
        document.UploadedBy.Should().Be(Agent);
    }

    [Fact]
    public void NotReviewed_is_the_default_so_a_row_that_skipped_the_factory_claims_nothing()
    {
        // Pending means "a validator owes us an answer". A row that never went through Register
        // must not make that claim, which is why NotReviewed is the zero value.
        default(KycDocumentReviewDecision).Should().Be(KycDocumentReviewDecision.NotReviewed);
    }

    [Fact]
    public void A_document_with_no_storage_reference_points_at_nothing()
    {
        var act = () => KycDocument.Register(
            Tenant, FileId, KycDocumentKind.Selfie,
            storageRef: "  ", contentType: "image/jpeg", sizeBytes: 1,
            sha256: new string('a', 64), uploadedBy: Agent, clock: TimeProvider.System);

        act.Should().Throw<DomainException>();
    }

    // ── the two decisions ───────────────────────────────────────────────────

    [Fact]
    public void An_accepted_document_records_its_validator_and_keeps_no_motive()
    {
        var document = Register();

        var result = document.Review(KycDocumentReviewDecision.Accepted, Validator, TimeProvider.System);

        result.IsSuccess.Should().BeTrue();
        document.ReviewDecision.Should().Be(KycDocumentReviewDecision.Accepted);
        document.ReviewedBy.Should().Be(Validator);
        document.ReviewedAt.Should().NotBeNull();
        document.RefusalReason.Should().BeNull("nothing has to be explained about an acceptance");
    }

    [Fact]
    public void An_accepted_document_discards_a_motive_it_was_handed_anyway()
    {
        // Storing one would make "why was this accepted" look like a recorded answer to a question
        // nobody asked.
        var document = Register();

        document.Review(KycDocumentReviewDecision.Accepted, Validator, TimeProvider.System, "ignoré");

        document.RefusalReason.Should().BeNull();
    }

    [Fact]
    public void A_refused_document_keeps_the_motive_trimmed()
    {
        var document = Register();

        var result = document.Review(
            KycDocumentReviewDecision.Refused, Validator, TimeProvider.System, $"  {Motive}  ");

        result.IsSuccess.Should().BeTrue();
        document.ReviewDecision.Should().Be(KycDocumentReviewDecision.Refused);
        document.RefusalReason.Should().Be(Motive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_refusal_without_a_motive_is_refused(string? blank)
    {
        // The agent has to re-photograph something; without a motive they are told to guess which
        // document and what was wrong with it.
        var document = Register();

        var result = document.Review(
            KycDocumentReviewDecision.Refused, Validator, TimeProvider.System, blank);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.DocumentReasonRequired);
        document.ReviewDecision.Should().Be(KycDocumentReviewDecision.Pending);
        document.ReviewedBy.Should().BeNull("a refused call must leave no actor behind");
    }

    // ── decide-once ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(KycDocumentReviewDecision.Accepted)]
    [InlineData(KycDocumentReviewDecision.Refused)]
    public void A_decided_document_cannot_be_decided_again(KycDocumentReviewDecision first)
    {
        var document = Register();
        document.Review(first, Validator, TimeProvider.System, Motive).IsSuccess.Should().BeTrue();

        var second = document.Review(
            KycDocumentReviewDecision.Accepted, Guid.NewGuid(), TimeProvider.System);

        second.IsFailure.Should().BeTrue();
        second.Error.Should().Be(KycErrors.DocumentAlreadyReviewed);
        document.ReviewDecision.Should().Be(first);
        document.ReviewedBy.Should().Be(Validator, "the first validator stays named");
    }

    [Theory]
    [InlineData(KycDocumentReviewDecision.Pending)]
    [InlineData(KycDocumentReviewDecision.NotReviewed)]
    public void A_state_is_not_a_decision(KycDocumentReviewDecision notADecision)
    {
        // Accepting either would be a way to un-review a document.
        var document = Register();

        var result = document.Review(notADecision, Validator, TimeProvider.System);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.InvalidTransition);
    }
}
