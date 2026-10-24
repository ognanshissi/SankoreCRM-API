namespace Sankore.Modules.Administration.Features.Products.SetBeneficiaryPolicy;

using FluentValidation;
using Sankore.Modules.Administration.PublicApi;

internal sealed class SetBeneficiaryPolicyValidator : AbstractValidator<SetBeneficiaryPolicyCommand>
{
    /// <summary>
    /// "Simplified" and "Full" are M02's two customer-facing vigilance outcomes. Spelled here as
    /// strings because M12 may not reference another module — and checked, because a typo would
    /// otherwise sit in the policy until a conversion was refused against a tier that does not
    /// exist.
    /// </summary>
    private static readonly string[] KycTiers = ["Simplified", "Full", KycRequirement.None];

    public SetBeneficiaryPolicyValidator()
    {
        RuleFor(c => c.ProductId).NotEmpty();

        RuleForEach(c => c.AllowedRoles).ChildRules(role =>
        {
            role.RuleFor(r => r.Code).NotEmpty().MaximumLength(50);
            role.RuleFor(r => r.Label).NotEmpty().MaximumLength(150);
            role.RuleFor(r => r.MinCount).GreaterThanOrEqualTo(0);
            role.RuleFor(r => r.MaxCount).GreaterThanOrEqualTo(1)
                .When(r => r.MaxCount.HasValue);
        });

        RuleFor(c => c.MaxChildren).GreaterThanOrEqualTo(0).When(c => c.MaxChildren.HasValue);
        RuleFor(c => c.ChildMaxAge).InclusiveBetween(0, 130).When(c => c.ChildMaxAge.HasValue);

        RuleFor(c => c.RequiredKycTier)
            .Must(t => KycTiers.Contains(t, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", KycTiers)}.")
            .When(c => !string.IsNullOrWhiteSpace(c.RequiredKycTier));

        RuleFor(c => c.IntegrationSystem).MaximumLength(50);

        RuleForEach(c => c.RequiredFieldsBeforeConversion)
            .NotEmpty().MaximumLength(80);

        // Caught here as well as in BeneficiaryPolicy.Create, deliberately: the aggregate's refusal
        // is a bare error code, and a duplicate role is a mistake in a form that deserves to say
        // WHICH field is wrong.
        RuleFor(c => c.AllowedRoles)
            .Must(roles => roles is null
                || roles.Select(r => r.Code?.Trim().ToUpperInvariant())
                        .Distinct(StringComparer.Ordinal).Count() == roles.Count)
            .WithMessage("Two designation roles share a code; codes are compared case-insensitively.");
    }
}
