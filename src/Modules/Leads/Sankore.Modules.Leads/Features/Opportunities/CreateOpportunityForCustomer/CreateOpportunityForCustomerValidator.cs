namespace Sankore.Modules.Leads.Features.Opportunities.CreateOpportunityForCustomer;

using FluentValidation;

internal sealed class CreateOpportunityForCustomerValidator
    : AbstractValidator<CreateOpportunityForCustomerCommand>
{
    public CreateOpportunityForCustomerValidator()
    {
        RuleFor(x => x.CustomerEntityId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Product).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(2000).When(x => x.Description is not null);
        RuleFor(x => x.EstimatedCurrency)
            .NotEmpty().WithMessage("Currency is required when amount is provided.")
            .MaximumLength(3)
            .When(x => x.EstimatedAmount.HasValue);
    }
}
