namespace Sankore.Modules.Administration.Features.Products.SetKycRemotePolicy;

using FluentValidation;
using Sankore.Modules.Administration.Domain;

/// <summary>
/// Shape and range checks, so a form gets a message naming the field that is wrong.
///
/// <para>
/// Everything here is ALSO checked by <see cref="KycRemotePolicy.Create"/>, deliberately and for
/// the reason <c>SetBeneficiaryPolicyValidator</c> gives: the value object answers with a bare
/// error code, and a screen deserves to be told which input to fix. The two checks that are NOT
/// duplicated are the ones this layer cannot make — whether the tenant allows machine approval,
/// and whether the required tier exceeds its ceiling — because both need a database read.
/// </para>
/// </summary>
internal sealed class SetKycRemotePolicyValidator : AbstractValidator<SetKycRemotePolicyCommand>
{
    public SetKycRemotePolicyValidator()
    {
        RuleFor(c => c.ProductId).NotEmpty();

        RuleFor(c => c.ValidationMode)
            .Must(m => KycRemotePolicyValues.NormalizeMode(m) is not null)
            .WithMessage(
                $"'{{PropertyName}}' must be one of: {string.Join(", ", KycRemotePolicyValues.Modes)}.")
            .When(c => !string.IsNullOrWhiteSpace(c.ValidationMode));

        RuleFor(c => c.RequiredTier)
            .Must(t => KycRemotePolicyValues.NormalizeTier(t) is not null)
            .WithMessage(
                $"'{{PropertyName}}' must be one of: {string.Join(", ", KycRemotePolicyValues.Tiers)}.")
            .When(c => !string.IsNullOrWhiteSpace(c.RequiredTier));

        RuleFor(c => c.MinAutoScore)
            .InclusiveBetween(KycRemotePolicyValues.MinimumAutoScoreFloor, 100);

        RuleFor(c => c.LinkValidityHours)
            .InclusiveBetween(1, KycRemotePolicyValues.MaximumLinkValidityHours);

        RuleForEach(c => c.RequiredDocuments)
            .Must(k => KycRemotePolicyValues.NormalizeDocumentKind(k) is not null)
            .WithMessage(
                "'{PropertyName}' must be one of: "
                + $"{string.Join(", ", KycRemotePolicyValues.DocumentKinds)}.");

        RuleForEach(c => c.AllowedDocumentTypes)
            .Must(t => KycRemotePolicyValues.NormalizeDocumentType(t) is not null)
            .WithMessage(
                "'{PropertyName}' must be one of: "
                + $"{string.Join(", ", KycRemotePolicyValues.DocumentTypes)}.");

        // An enabled line must name at least one accepted identity document, or the customer
        // reaches the public page with nothing to choose from.
        RuleFor(c => c.AllowedDocumentTypes)
            .Must(types => types is { Count: > 0 })
            .WithMessage(
                "At least one accepted identity document type is required to enable remote KYC.")
            .When(c => c.Enabled);
    }
}
