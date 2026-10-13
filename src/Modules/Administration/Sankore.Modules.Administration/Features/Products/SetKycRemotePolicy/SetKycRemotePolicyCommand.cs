namespace Sankore.Modules.Administration.Features.Products.SetKycRemotePolicy;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Configures whether a product line may be KYC'd at a distance after a conversion, and how such a
/// file is validated (KRM-B-01).
///
/// <para>
/// A full replacement, not a patch — the same reasoning as
/// <c>SetBeneficiaryPolicyCommand</c>: the fields constrain each other, and a PATCH able to carry
/// one of them would let a line end up Automatic with a score floor from a previous policy, or
/// enabled with no accepted document type. <c>ICommand</c> brings the transaction and the audit
/// row; <c>IResourceCommand</c> names the product in it, which KRM-B-01 asks for explicitly
/// ("toute modification auditée").
/// </para>
/// </summary>
/// <param name="Enabled">
/// Whether a conversion on this line sends the new customer a collection link at all.
/// </param>
/// <param name="ValidationMode">
/// "Manual" or "Automatic". Automatic is refused when the tenant has not enabled machine approval,
/// or when <paramref name="RequiredTier"/> exceeds the tenant's ceiling — the product screen must
/// not be able to save a configuration the gate will then ignore.
/// </param>
/// <param name="RequiredTier">"Simplified" | "Full", or null to state no level.</param>
/// <param name="MinAutoScore">Score floor for an automatic approval, 50..100.</param>
/// <param name="RequiredDocuments">
/// <c>KycDocumentKind</c> names the customer must send — "ProofOfAddress" for an insurance product
/// sold at a distance (CIMA LBC/FT article 13.6).
/// </param>
/// <param name="AllowedDocumentTypes">
/// <c>KycDocumentType</c> names accepted as identity document. Required as soon as
/// <paramref name="Enabled"/> is true.
/// </param>
/// <param name="LinkValidityHours">Lifetime of the link in hours, 1..336.</param>
internal sealed record SetKycRemotePolicyCommand(
    Guid ProductId,
    bool Enabled = false,
    string? ValidationMode = null,
    string? RequiredTier = null,
    int MinAutoScore = Domain.KycRemotePolicyValues.DefaultMinAutoScore,
    IReadOnlyList<string>? RequiredDocuments = null,
    IReadOnlyList<string>? AllowedDocumentTypes = null,
    int LinkValidityHours = Domain.KycRemotePolicyValues.DefaultLinkValidityHours
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductSpeciality";
    public string? ResourceId => ProductId.ToString();
}
