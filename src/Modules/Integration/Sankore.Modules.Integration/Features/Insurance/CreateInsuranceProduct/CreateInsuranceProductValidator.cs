namespace Sankore.Modules.Integration.Features.Insurance.CreateInsuranceProduct;

using FluentValidation;

internal sealed class CreateInsuranceProductValidator : AbstractValidator<CreateInsuranceProductCommand>
{
    public CreateInsuranceProductValidator()
    {
        RuleFor(c => c.ConnectionId)
            .NotEmpty()
            .OverridePropertyName("connectionId");

        RuleFor(c => c.InsurerProductCode)
            .NotEmpty().WithMessage(
                "The insurer's own product code is required: it is what travels in a "
                + "subscription, and the tenant's label is not interchangeable with it.")
            .MaximumLength(100)
            .OverridePropertyName("insurerProductCode");

        RuleFor(c => c.Body)
            .NotNull()
            .OverridePropertyName("body");

        // The shared rules, so create and update cannot drift on what they accept.
        RuleFor(c => c.Body)
            .SetValidator(new InsuranceProductBodyValidator()!)
            .When(c => c.Body is not null);
    }
}
