namespace Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;

using FluentValidation;

internal sealed class CreateOpportunityValidator : AbstractValidator<CreateOpportunityCommand>
{
    public CreateOpportunityValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        // Optional: a need is noted before it is matched to a product — see Opportunity.CreateFromLead.
        RuleFor(x => x.Product).MaximumLength(100);
        RuleFor(x => x.LeadId).NotEmpty();
        RuleFor(x => x.Description).MaximumLength(2000).When(x => x.Description is not null);
        // Optional: an amount without one takes the institution's currency (the handler decides).
        RuleFor(x => x.EstimatedCurrency).MaximumLength(3);
    }
}
