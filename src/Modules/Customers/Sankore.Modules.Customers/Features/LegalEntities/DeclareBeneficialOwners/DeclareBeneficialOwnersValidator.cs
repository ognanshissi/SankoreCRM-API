namespace Sankore.Modules.Customers.Features.LegalEntities.DeclareBeneficialOwners;

using FluentValidation;

/// <summary>
/// Shape of the declaration. The AML rules themselves (100 % ceiling, mandatory manager
/// below the tenant threshold) live in the handler: they need the tenant setting and the
/// target client, which a validator has no business loading.
/// </summary>
public sealed class DeclareBeneficialOwnersValidator : AbstractValidator<DeclareBeneficialOwnersCommand>
{
    public DeclareBeneficialOwnersValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();

        // Every sensitive change is motivated — the handler re-checks and returns
        // REASON_REQUIRED, so the rule holds for non-HTTP callers too.
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).MaximumLength(500);

        RuleFor(x => x.Owners).NotNull();

        RuleForEach(x => x.Owners).ChildRules(owner =>
        {
            owner.RuleFor(o => o.ControlType).IsInEnum();

            owner.RuleFor(o => o.OwnershipPercentage)
                .InclusiveBetween(0m, 100m)
                .WithMessage("An ownership percentage must fall between 0 and 100.");

            // Either an existing client OR an external identity — never both, never neither.
            owner.RuleFor(o => o)
                .Must(o => (o.LinkedClientId.HasValue && o.LinkedClientId.Value != Guid.Empty)
                           ^ !string.IsNullOrWhiteSpace(o.ExternalFullName))
                .WithMessage("Provide exactly one of LinkedClientId or ExternalFullName.")
                .OverridePropertyName(nameof(BeneficialOwnerInput.LinkedClientId));

            owner.RuleFor(o => o.ExternalFullName).MaximumLength(150);
            owner.RuleFor(o => o.ExternalNationality).MaximumLength(100);
            owner.RuleFor(o => o.ExternalDocumentNumber).MaximumLength(60);
        });
    }
}
