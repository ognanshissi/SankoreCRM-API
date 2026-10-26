namespace Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;

using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Prepares the insurance subscription of a SIGNED holding (ASS-04): the insurer's product, the
/// premium the client agreed to, how it is paid, and the consent proof. The chain of ASS-05 — collect
/// the premium, then ask the insurer, then give the premium back if the insurer refuses — starts
/// when the holding is sent (<c>POST holdings/{id}/submission</c>), not here.
///
/// <para>
/// <b>The holding is the contract</b> (product owner, 2026-10-25): an insurance subscription is
/// always a <c>ProductHolding</c>, opened by a won project or from the client's record, and the
/// holding becomes Active only when the insurer issues the policy. This command used to subscribe a
/// customer with no contract record at all, which left two paths and a policy nobody's record
/// pointed at. The effective date and the beneficiary clause are the holding's; the questionnaires
/// are the won project's.
/// </para>
///
/// <para>
/// <b>The premium is collected FIRST, and the order is the design.</b> A premium collected against a
/// policy the insurer then refuses can be put right; a policy issued against a premium that was never
/// collected is an insurer's receivable against a customer nobody charged. Two ways to collect it:
/// a debit on the client's core banking account, or — for an institution with no core banking — the
/// payment the agent took, recorded with its method, reference and date.
/// </para>
///
/// <para>
/// <b>The consent proof lands in the SAME transaction as the subscription</b>: evidence recorded by
/// a second request can fail on its own, and a subscription whose consent was collected but not
/// stored is worse than one that was never taken. The 2024 CIMA regulation on distribution by
/// electronic means requires each step to carry a timestamped, opposable record.
/// </para>
/// </summary>
internal sealed record SubscribeInsurancePolicyCommand(
    Guid HoldingId,
    Guid ProductId,
    PremiumPaymentMode PaymentMode,
    string? AccountRef,
    RecordedPaymentMethod? PaymentMethod,
    string? PaymentReference,
    DateOnly? PaidOn,
    decimal? PremiumAmount,

    /// <summary>
    /// The premium's currency. Optional: it falls back to the product's own, which is where a
    /// fixed-price catalogue entry carries it.
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

    // ── The 60-day derogation (insurance lot 2) ─────────────────────────────
    string? CommitmentReference = null,
    DateOnly? CommitmentSignedOn = null,

    // ── Lot 4: a third-party payer, an existing client with an approved KYC ──
    Guid? PremiumPayerClientId = null
) : IRequest<Result<SubscribeInsurancePolicyResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductHolding";

    public string? ResourceId => HoldingId.ToString();
}

/// <param name="AlreadyExisted">
/// True when the holding's subscription had already left (its premium collected or being
/// collected). The command SUCCEEDS with the existing id: the client is being subscribed, and a
/// second collection is exactly what the unique indexes exist to prevent.
/// </param>
/// <param name="DebitCommandId">Always null now: the debit is queued when the holding is sent.</param>
public sealed record SubscribeInsurancePolicyResult(
    Guid SubscriptionId,
    string Status,
    bool AlreadyExisted,
    Guid? DebitCommandId);

public sealed record RecordPremiumPaymentRequest(
    RecordedPaymentMethod PaymentMethod,
    string PaymentReference,
    DateOnly PaidOn);

/// <summary>
/// The body. <c>paymentMode</c> decides which half is read: <c>accountRef</c> for a core banking
/// debit, <c>paymentMethod</c> + <c>paymentReference</c> + <c>paidOn</c> for a premium the agent
/// collected, nothing for <c>InsurerCollects</c>, and <c>commitmentReference</c> +
/// <c>commitmentSignedOn</c> for the 60-day derogation.
/// </summary>
public sealed record SubscribeInsurancePolicyRequest(
    Guid HoldingId,
    Guid ProductId,
    PremiumPaymentMode PaymentMode,
    ConsentChannel ConsentChannel,
    string ConsentTextVersion,
    DateTimeOffset ConsentedAt,
    string? AccountRef = null,
    RecordedPaymentMethod? PaymentMethod = null,
    string? PaymentReference = null,
    DateOnly? PaidOn = null,
    decimal? PremiumAmount = null,
    string? Currency = null,
    string? ConsentTextSha256 = null,
    string? ConsentEvidence = null,
    string? ConsentEvidenceStorageRef = null,
    string? ConsentEvidenceSha256 = null,
    string? ConsentEvidenceContentType = null,
    string? CommitmentReference = null,
    DateOnly? CommitmentSignedOn = null,
    Guid? PremiumPayerClientId = null);
