namespace Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;

using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Subscribes a customer to one of the tenant's insurance products (ASS-04), and starts the
/// three-step chain of ASS-05: debit the premium, subscribe at the insurer, reverse the debit if
/// the insurer refuses.
///
/// <para>
/// <b>The debit goes FIRST, and the order is the design.</b> A premium collected against a policy
/// the insurer then refuses can be put right by a reversal; a policy issued against a premium that
/// was never collected is an insurer's receivable against a customer nobody debited. The reversal
/// is the compensating step of a saga, not an afterthought.
/// </para>
///
/// <para>
/// <b>The consent proof lands in the SAME transaction as the subscription.</b> That is the lesson
/// BNF-B-18 already paid for on a lead's health declaration: evidence recorded by a second request
/// can fail on its own, and a subscription whose consent was collected but not stored is worse
/// than one that was never taken. The 2024 CIMA regulation on distribution by electronic means
/// requires each step to carry a timestamped, opposable record — so the proof is not optional
/// paperwork, it is the thing that makes the subscription lawful.
/// </para>
///
/// <para>
/// The evidence fields are marked sensitive: the audit trail records the field NAMES and never a
/// value, the convention every encrypted field in this solution follows.
/// </para>
/// </summary>
internal sealed record SubscribeInsurancePolicyCommand(
    Guid ProductId,
    Guid CrmCustomerId,
    DateOnly EffectiveDate,
    string AccountRef,
    decimal? PremiumAmount,

    /// <summary>
    /// The premium's currency. Optional: it falls back to the product's own, which is where a
    /// fixed-price catalogue entry carries it. Required in effect, because
    /// <c>InsuranceSubscription</c> refuses an amount without one — an amount with no currency is
    /// not a sum of money.
    /// </summary>
    string? Currency,

    // ── Consent (CIMA 2024, règlement sur la distribution par voie numérique) ──
    ConsentChannel ConsentChannel,
    string ConsentTextVersion,
    DateTimeOffset ConsentedAt,
    string? ConsentTextSha256,
    [property: SensitiveData] string? ConsentEvidence,
    string? ConsentEvidenceStorageRef,
    string? ConsentEvidenceSha256,
    string? ConsentEvidenceContentType,

    // ── Medical underwriting, when the product calls for it ─────────────────
    string? MedicalQuestionnaireCode,
    [property: SensitiveData] string? MedicalAnswers,
    bool RequiresMedicalUnderwriting,

    // ── Beneficiaries ───────────────────────────────────────────────────────
    [property: SensitiveData] string? Beneficiaries
) : IRequest<Result<SubscribeInsurancePolicyResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "InsuranceSubscription";

    /// <summary>
    /// Null: the subscription's id does not exist until the handler has created it. The audit row
    /// still names the resource TYPE and the actor, which is what makes the entry findable — and
    /// the handler logs the id it minted.
    /// </summary>
    public string? ResourceId => null;
}

/// <param name="AlreadyExisted">
/// True when a subscription for this customer, product and effective date was already in flight.
/// The command SUCCEEDS in that case, carrying the existing id: the caller asked for the customer
/// to be subscribed and the customer is being subscribed. A second debit is exactly what
/// <c>ux_ins_subscription_idempotency</c> exists to prevent.
/// </param>
/// <param name="DebitCommandId">
/// The queued debit. Null only when the subscription already existed and its own debit had not
/// yet been attached, which a caller treats as "in flight" rather than as a failure.
/// </param>
public sealed record SubscribeInsurancePolicyResult(
    Guid SubscriptionId,
    string Status,
    bool AlreadyExisted,
    Guid? DebitCommandId);

/// <summary>The body. The customer and the product travel in it rather than in the route, because
/// a subscription is created against neither on its own.</summary>
public sealed record SubscribeInsurancePolicyRequest(
    Guid ProductId,
    Guid CrmCustomerId,
    DateOnly EffectiveDate,
    string AccountRef,
    ConsentChannel ConsentChannel,
    string ConsentTextVersion,
    DateTimeOffset ConsentedAt,
    decimal? PremiumAmount = null,
    string? Currency = null,
    string? ConsentTextSha256 = null,
    string? ConsentEvidence = null,
    string? ConsentEvidenceStorageRef = null,
    string? ConsentEvidenceSha256 = null,
    string? ConsentEvidenceContentType = null,
    string? MedicalQuestionnaireCode = null,
    string? MedicalAnswers = null,
    bool RequiresMedicalUnderwriting = false,
    string? Beneficiaries = null);
