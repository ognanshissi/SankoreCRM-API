using FluentValidation;

namespace Sankore.Modules.Administration.Features.Products.CreateProduct;

internal sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(50)
            .Matches("^[A-Za-z0-9_-]+$")
            .WithMessage("Code must contain only letters, digits, hyphens, or underscores.");
        RuleFor(x => x.Description).MaximumLength(500);
    }
}
