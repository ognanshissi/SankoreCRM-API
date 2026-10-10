namespace Sankore.Modules.Integration.Features.Insurance.UpdateInsuranceProduct;

using FluentValidation;

internal sealed class UpdateInsuranceProductValidator : AbstractValidator<UpdateInsuranceProductCommand>
{
    public UpdateInsuranceProductValidator()
    {
        RuleFor(c => c.ProductId)
            .NotEmpty()
            .OverridePropertyName("productId");

        RuleFor(c => c.Body)
            .NotNull()
            .OverridePropertyName("body");

        // The same rule set the create uses: a field refused on creation and accepted on edit is
        // how an invalid row gets in through the back door.
        RuleFor(c => c.Body)
            .SetValidator(new InsuranceProductBodyValidator()!)
            .When(c => c.Body is not null);
    }
}
