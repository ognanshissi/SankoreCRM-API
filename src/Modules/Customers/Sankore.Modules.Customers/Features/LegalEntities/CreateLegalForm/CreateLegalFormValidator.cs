namespace Sankore.Modules.Customers.Features.LegalEntities.CreateLegalForm;

using FluentValidation;

public sealed class CreateLegalFormValidator : AbstractValidator<CreateLegalFormCommand>
{
    public CreateLegalFormValidator()
    {
        // Codes travel in URLs (DELETE legal-forms/{code}) and are compared as-is against
        // Client.LegalFormCode, so they stay short and free of separators.
        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(30)
            .Matches("^[A-Za-z0-9._-]+$")
            .WithMessage("A legal-form code accepts letters, digits, dot, dash and underscore only.");

        RuleFor(x => x.Label).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DisplayOrder).GreaterThanOrEqualTo(0);
    }
}
