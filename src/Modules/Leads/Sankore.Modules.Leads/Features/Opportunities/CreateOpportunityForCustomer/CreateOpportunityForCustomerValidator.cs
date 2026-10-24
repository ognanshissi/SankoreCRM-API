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
        // Optional: an amount without one takes the institution's currency (the handler decides).
        RuleFor(x => x.EstimatedCurrency).MaximumLength(3);
    }
}
