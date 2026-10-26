namespace Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// The state of one subscription attempt — ASS-05's « l'état global de la souscription
/// consultable », which is a requirement and not a convenience.
///
/// <para>
/// No command row can answer it: each knows only its own outcome, and the same <c>Rejected</c>
/// means "the insurer said no" on one and "the customer was not refunded" on another. This is the
/// screen an operator works a <c>PremiumRefundDue</c> from.
/// </para>
/// </summary>
internal sealed record GetInsuranceSubscriptionQuery(Guid SubscriptionId)
    : IRequest<Result<InsuranceSubscriptionDto>>;

/// <param name="OwesRefund">
/// The premium was collected, the insurer refused, and the reversal has not gone through. The one
/// state a human has to act on, so it is a flag and not something a screen re-derives from the
/// status name.
/// </param>
/// <param name="ReversalAttempts">
/// How many reversals have been tried. Exposed because a refused reversal is NOT retried
/// automatically: an operator re-attempting needs to know the core banking system has already
/// said no once.
/// </param>
/// <param name="CbsDebitReference">
/// The reference the debit returned — the only input a reversal can be keyed on, which is why an
/// operator sees it. Not a credential: it is the same class of value as an external policy number.
/// </param>
public sealed record InsuranceSubscriptionDto(
    Guid Id,
    Guid ConnectionId,
    Guid CrmCustomerId,
    Guid ProductId,
    DateOnly EffectiveDate,
    string Status,
    decimal PremiumAmount,
    string Currency,
    string? CbsDebitReference,
    DateTimeOffset? DebitedAt,
    string? CbsReversalReference,
    DateTimeOffset? ReversedAt,
    int ReversalAttempts,
    bool OwesRefund,
    Guid? DebitCommandId,
    Guid? SubscribeCommandId,
    Guid? ReversalCommandId,
    Guid? PolicyId,
    string? InsurerPolicyReference,
    string? FailureCode,
    string? FailureDetail,
    bool HasConsentProof,
    bool HasMedicalQuestionnaire,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    // ── Lots 2-4 ────────────────────────────────────────────────────────────
    public Guid? HoldingId { get; init; }
    public string? PaymentMode { get; init; }
    public DateTimeOffset? PremiumPaidAt { get; init; }
    public bool PremiumOutstanding { get; init; }
    public DateOnly? PaymentDueBy { get; init; }
    public DateTimeOffset? SentToInsurerAt { get; init; }
    public DateTimeOffset? IssuedAt { get; init; }
    public string? Underwriting { get; init; }
    public decimal? SurchargeAmount { get; init; }
    public string? UnderwritingExclusions { get; init; }
    public string? MedicalExamsRequested { get; init; }
    public string? UnderwritingReference { get; init; }
    public bool AwaitsClientAnswer { get; init; }
    public string? ClientAnswer { get; init; }
    public bool SurchargeOutstanding { get; init; }
    public bool SilenceMayBeConfirmed { get; init; }
    public Guid? PremiumPayerClientId { get; init; }

    internal static InsuranceSubscriptionDto Build(
        Sankore.Modules.Integration.Domain.InsuranceSubscription saga, bool hasConsent, bool hasMedical, DateTimeOffset clockNow)
        => new InsuranceSubscriptionDto(
            Id: saga.Id,
            ConnectionId: saga.ConnectionId,
            CrmCustomerId: saga.CrmCustomerId,
            ProductId: saga.ProductId,
            EffectiveDate: saga.EffectiveDate,
            Status: saga.Status.ToString(),
            PremiumAmount: saga.PremiumAmount,
            Currency: saga.Currency,
            CbsDebitReference: saga.CbsDebitReference,
            DebitedAt: saga.DebitedAt,
            CbsReversalReference: saga.CbsReversalReference,
            ReversedAt: saga.ReversedAt,
            ReversalAttempts: saga.ReversalAttempts,
            // Through the aggregate's own definition rather than a status comparison repeated
            // here: it is what the refund queue is built on.
            OwesRefund: saga.OwesRefund,
            DebitCommandId: saga.DebitCommandId,
            SubscribeCommandId: saga.SubscribeCommandId,
            ReversalCommandId: saga.ReversalCommandId,
            PolicyId: saga.PolicyId,
            InsurerPolicyReference: saga.InsurerPolicyReference,
            FailureCode: saga.FailureCode,
            FailureDetail: saga.FailureDetail,
            HasConsentProof: hasConsent,
            HasMedicalQuestionnaire: hasMedical,
            CreatedAt: saga.CreatedAt,
            UpdatedAt: saga.UpdatedAt)
        {
            HoldingId = saga.CrmHoldingId,
            PaymentMode = saga.PaymentMode.ToString(),
            PremiumPaidAt = saga.PremiumPaidAt,
            PremiumOutstanding = saga.IsPremiumOutstanding,
            PaymentDueBy = saga.PaymentDueBy,
            SentToInsurerAt = saga.SentToInsurerAt,
            IssuedAt = saga.IssuedAt,
            Underwriting = saga.Underwriting?.ToString(),
            SurchargeAmount = saga.SurchargeAmount,
            UnderwritingExclusions = saga.UnderwritingExclusions,
            MedicalExamsRequested = saga.MedicalExamsRequested,
            UnderwritingReference = saga.UnderwritingReference,
            AwaitsClientAnswer = saga.AwaitsClientAnswer,
            ClientAnswer = saga.ClientAnswer?.ToString(),
            SurchargeOutstanding = saga.IsSurchargeOutstanding,
            // The insurer's 15 days run out: its silence may now be confirmed as acceptance.
            SilenceMayBeConfirmed = saga.SentToInsurerAt is { } sent && saga.IssuedAt is null && saga.Underwriting is null
                && !saga.IsEnded && clockNow >= sent.AddDays(15),
            PremiumPayerClientId = saga.PremiumPayerClientId,
        };
}

internal sealed class GetInsuranceSubscriptionHandler(IntegrationDbContext db, TimeProvider clock)
    : IRequestHandler<GetInsuranceSubscriptionQuery, Result<InsuranceSubscriptionDto>>
{
    public async Task<Result<InsuranceSubscriptionDto>> Handle(
        GetInsuranceSubscriptionQuery query, CancellationToken ct)
    {
        // The tenant query filter does the isolation: a subscription of another tenant is simply
        // absent, and the endpoint answers NOT_FOUND rather than FORBIDDEN.
        var saga = await db.InsuranceSubscriptions
            .FirstOrDefaultAsync(s => s.Id == query.SubscriptionId, ct);

        if (saga is null)
            return Result.Fail<InsuranceSubscriptionDto>(InsuranceErrors.SubscriptionNotFound);

        // Whether the evidence EXISTS, never its content: the consent payload and the medical
        // answers are encrypted, and a compliance screen needs to know they are on file — not to
        // read them. Two counts rather than two joins, so neither row is materialised.
        var hasConsent = await db.ConsentProofs
            .AnyAsync(p => p.SubscriptionId == saga.Id, ct);

        var hasMedical = await db.MedicalQuestionnaires
            .AnyAsync(q => q.SubscriptionId == saga.Id, ct);

        return Result.Ok(InsuranceSubscriptionDto.Build(saga, hasConsent, hasMedical, clock.GetUtcNow()));
    }
}
